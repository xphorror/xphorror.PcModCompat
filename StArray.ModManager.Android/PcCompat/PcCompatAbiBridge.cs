namespace StArray.ModManager.Android.PcCompat;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

public static class PcCompatAbiBridge
{
    public static object? BoxUnboxedValue<T>(Il2CppSystem.Object? source) where T : unmanaged
        => source is null ? null : source.Unbox<T>();

    public static Il2CppSystem.Nullable<T>? ToIl2CppNullable<T>(T? source) where T : struct
        => source.HasValue ? new Il2CppSystem.Nullable<T>(source.Value) : default;

    public static TIl2Cpp? ToIl2CppDelegate<TIl2Cpp>(Delegate? source)
        where TIl2Cpp : Il2CppObjectBase
        => source is null ? null : DelegateSupport.ConvertDelegate<TIl2Cpp>(source);
}

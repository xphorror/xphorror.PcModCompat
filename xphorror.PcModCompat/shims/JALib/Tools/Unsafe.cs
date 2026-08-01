namespace JALib.Tools;

public static class Unsafe
{
    public static T AsUnsafe<T>(this object value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return System.Runtime.CompilerServices.Unsafe.As<object, T>(ref value);
    }

    public static TTo AsUnsafe<TFrom, TTo>(this TFrom value)
        where TFrom : struct
        where TTo : struct
        => System.Runtime.CompilerServices.Unsafe.As<TFrom, TTo>(ref value);

    public static ref T UnboxUnsafe<T>(this object box)
        where T : struct
        => ref System.Runtime.CompilerServices.Unsafe.Unbox<T>(box);

    public static IntPtr AsPointer(this object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return System.Runtime.CompilerServices.Unsafe.As<object, IntPtr>(ref value);
    }

    public static UIntPtr AsUnsignedPointer(this object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return System.Runtime.CompilerServices.Unsafe.As<object, UIntPtr>(ref value);
    }
}

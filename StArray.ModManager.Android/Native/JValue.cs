using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>JNI jvalue union used by the Call*MethodA entry points.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct JValue
{
    [FieldOffset(0)] public byte Z;
    [FieldOffset(0)] public sbyte B;
    [FieldOffset(0)] public char C;
    [FieldOffset(0)] public short S;
    [FieldOffset(0)] public int I;
    [FieldOffset(0)] public long J;
    [FieldOffset(0)] public float F;
    [FieldOffset(0)] public double D;
    [FieldOffset(0)] public nint L;
}

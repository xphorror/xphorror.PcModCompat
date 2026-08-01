using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static unsafe class PcCompatAndroidImGuiStyleBridge
{
    private const string FixedWidthIcall =
        "UnityEngine.GUIStyle::set_fixedWidth_Injected";
    private const string FixedHeightIcall =
        "UnityEngine.GUIStyle::set_fixedHeight_Injected";
    private static readonly object ResolveLock = new();
    private static nint s_styleClass;
    private static nint s_nativePointerField;
    private static SetFixedWidthInjected? s_setFixedWidth;
    private static SetFixedHeightInjected? s_setFixedHeight;

    [ModuleInitializer]
    internal static void Register()
    {
        PcCompatManagedImGuiBridge.RegisterFixedWidthSetter(SetFixedWidth);
        PcCompatManagedImGuiBridge.RegisterFixedHeightSetter(SetFixedHeight);
    }

    private static void SetFixedWidth(object style, float value)
    {
        var nativeStyle = GetNativeStyle(style);
        s_setFixedWidth!(nativeStyle, value);
    }

    private static void SetFixedHeight(object style, float value)
    {
        var nativeStyle = GetNativeStyle(style);
        s_setFixedHeight!(nativeStyle, value);
    }

    private static nint GetNativeStyle(object style)
    {
        if (style is not Il2CppObjectBase proxy || proxy.Pointer == nint.Zero)
            throw new InvalidOperationException("GUIStyle bridge received an invalid IL2CPP proxy.");

        var styleClass = IL2CPP.RequireIl2CppClass(
            IL2CPP.il2cpp_object_get_class(proxy.Pointer),
            "UnityEngine.GUIStyle instance class");
        EnsureResolved(styleClass);

        nint nativeStyle = nint.Zero;
        IL2CPP.il2cpp_field_get_value(proxy.Pointer, s_nativePointerField, &nativeStyle);
        if (nativeStyle == nint.Zero)
            throw new InvalidOperationException("GUIStyle.m_Ptr is null.");
        return nativeStyle;
    }

    private static void EnsureResolved(nint styleClass)
    {
        if (Volatile.Read(ref s_styleClass) == styleClass &&
            Volatile.Read(ref s_nativePointerField) != nint.Zero &&
            Volatile.Read(ref s_setFixedWidth) is not null &&
            Volatile.Read(ref s_setFixedHeight) is not null)
            return;

        lock (ResolveLock)
        {
            if (s_styleClass != styleClass || s_nativePointerField == nint.Zero)
            {
                s_nativePointerField = IL2CPP.GetIl2CppField(styleClass, "m_Ptr");
                s_styleClass = styleClass;
            }
            if (s_setFixedWidth is null)
            {
                var function = IL2CPP.il2cpp_resolve_icall(FixedWidthIcall);
                if (function == nint.Zero)
                    throw new MissingMethodException($"Unity icall was not found: {FixedWidthIcall}");
                s_setFixedWidth = Marshal.GetDelegateForFunctionPointer<SetFixedWidthInjected>(function);
            }
            if (s_setFixedHeight is null)
            {
                var function = IL2CPP.il2cpp_resolve_icall(FixedHeightIcall);
                if (function == nint.Zero)
                    throw new MissingMethodException($"Unity icall was not found: {FixedHeightIcall}");
                s_setFixedHeight = Marshal.GetDelegateForFunctionPointer<SetFixedHeightInjected>(function);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFixedWidthInjected(nint nativeStyle, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFixedHeightInjected(nint nativeStyle, float value);
}

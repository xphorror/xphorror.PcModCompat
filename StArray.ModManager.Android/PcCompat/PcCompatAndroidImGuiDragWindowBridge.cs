using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static unsafe class PcCompatAndroidImGuiDragWindowBridge
{
    private const string DragWindowIcall =
        "UnityEngine.GUI::DragWindow_Injected";
    private static readonly object ResolveLock = new();
    private static DragWindowInjected? s_dragWindow;

    [ModuleInitializer]
    internal static void Register()
        => PcCompatManagedImGuiBridge.RegisterDragWindowBridge(DragWindow);

    private static void DragWindow(object position)
    {
        EnsureResolved();
        var rect = new RectValue
        {
            X = ReadFloat(position, "m_XMin", "x"),
            Y = ReadFloat(position, "m_YMin", "y"),
            Width = ReadFloat(position, "m_Width", "width"),
            Height = ReadFloat(position, "m_Height", "height")
        };
        s_dragWindow!(ref rect);
    }

    private static float ReadFloat(object value, params string[] names)
    {
        var type = value.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance;
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return Convert.ToSingle(field.GetValue(value), CultureInfo.InvariantCulture);

            var property = type.GetProperty(name, flags);
            if (property?.GetMethod != null)
                return Convert.ToSingle(property.GetValue(value), CultureInfo.InvariantCulture);
        }

        throw new MissingMemberException(
            $"UnityEngine.Rect proxy does not expose any of: {string.Join(", ", names)}.");
    }

    private static void EnsureResolved()
    {
        if (Volatile.Read(ref s_dragWindow) is not null)
            return;

        lock (ResolveLock)
        {
            s_dragWindow ??= Resolve<DragWindowInjected>(DragWindowIcall);
        }
    }

    private static T Resolve<T>(string icall) where T : Delegate
    {
        var function = IL2CPP.il2cpp_resolve_icall(icall);
        if (function == nint.Zero)
            throw new MissingMethodException($"Unity icall was not found: {icall}");
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectValue
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DragWindowInjected(ref RectValue position);
}

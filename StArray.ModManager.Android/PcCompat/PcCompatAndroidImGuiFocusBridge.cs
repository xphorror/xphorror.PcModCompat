using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static unsafe class PcCompatAndroidImGuiFocusBridge
{
    private const string SetNextControlNameIcall =
        "UnityEngine.GUI::SetNextControlName_Injected";
    private const string GetNameOfFocusedControlIcall =
        "UnityEngine.GUI::GetNameOfFocusedControl_Injected";
    private const string FreeIcall =
        "UnityEngine.Bindings.BindingsAllocator::Free";
    private static readonly object ResolveLock = new();
    private static SetNextControlNameInjected? s_setNextControlName;
    private static GetNameOfFocusedControlInjected? s_getNameOfFocusedControl;
    private static FreeBindingsMemory? s_freeBindingsMemory;

    [ModuleInitializer]
    internal static void Register()
    {
        PcCompatManagedImGuiBridge.RegisterControlFocusBridge(
            SetNextControlName,
            GetNameOfFocusedControl);
    }

    private static void SetNextControlName(string? name)
    {
        EnsureResolved();
        if (name is null)
        {
            var nullSpan = default(ManagedSpanWrapper);
            s_setNextControlName!(ref nullSpan);
            return;
        }
        if (name.Length == 0)
        {
            var emptySpan = new ManagedSpanWrapper((nint)1, 0);
            s_setNextControlName!(ref emptySpan);
            return;
        }

        fixed (char* begin = name)
        {
            var span = new ManagedSpanWrapper((nint)begin, name.Length);
            s_setNextControlName!(ref span);
        }
    }

    private static string? GetNameOfFocusedControl()
    {
        EnsureResolved();
        s_getNameOfFocusedControl!(out var result);
        if (result.Length == 0)
            return result.Begin == nint.Zero ? null : string.Empty;
        if (result.Begin == nint.Zero || result.Length < 0)
            throw new InvalidDataException("Unity returned an invalid focused-control string span.");

        try
        {
            return new string((char*)result.Begin, 0, result.Length);
        }
        finally
        {
            s_freeBindingsMemory!(result.Begin);
        }
    }

    private static void EnsureResolved()
    {
        if (Volatile.Read(ref s_setNextControlName) is not null &&
            Volatile.Read(ref s_getNameOfFocusedControl) is not null &&
            Volatile.Read(ref s_freeBindingsMemory) is not null)
        {
            return;
        }

        lock (ResolveLock)
        {
            s_setNextControlName ??= Resolve<SetNextControlNameInjected>(SetNextControlNameIcall);
            s_getNameOfFocusedControl ??=
                Resolve<GetNameOfFocusedControlInjected>(GetNameOfFocusedControlIcall);
            s_freeBindingsMemory ??= Resolve<FreeBindingsMemory>(FreeIcall);
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
    private readonly struct ManagedSpanWrapper(nint begin, int length)
    {
        public readonly nint Begin = begin;
        public readonly int Length = length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetNextControlNameInjected(ref ManagedSpanWrapper name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetNameOfFocusedControlInjected(out ManagedSpanWrapper result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeBindingsMemory(nint pointer);
}

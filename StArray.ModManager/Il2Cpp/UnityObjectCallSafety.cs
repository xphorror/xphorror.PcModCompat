using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Il2Cpp;

internal enum UnityObjectCallKind
{
    None,
    DestroyObject,
    DestroyObjectDelayed,
    DestroyImmediateObject,
    DestroyImmediateObjectAllowAssets,
    DontDestroyOnLoadObject,
    ObjectImplicit,
}

internal static unsafe class UnityObjectCallSafety
{
    private const string UnityObjectType = "UnityEngine.Object";
    private static readonly IProcessMemoryReader MemoryReader = NativeProcessMemoryReader.Instance;

    private static GuardedVoidObjectMethod? _destroyObject;
    private static GuardedVoidObjectFloatMethod? _destroyObjectDelayed;
    private static GuardedVoidObjectMethod? _destroyImmediateObject;
    private static GuardedVoidObjectBooleanMethod? _destroyImmediateObjectAllowAssets;
    private static GuardedVoidObjectMethod? _dontDestroyOnLoadObject;
    private static GuardedBooleanObjectMethod? _objectImplicit;

    internal static nint GetFunctionPointer(nint method, nint target)
    {
        if (!OperatingSystem.IsAndroid() || method == nint.Zero || target == nint.Zero)
            return target;

        try
        {
            var declaringType = Il2CppFunctions.il2cpp_method_get_declaring_type(method);
            if (declaringType == nint.Zero)
                return target;

            var namespaze = Marshal.PtrToStringAnsi(
                Il2CppFunctions.il2cpp_class_get_namespace(declaringType)) ?? "";
            var typeName = Marshal.PtrToStringAnsi(
                Il2CppFunctions.il2cpp_class_get_name(declaringType)) ?? "";
            var methodName = Marshal.PtrToStringAnsi(
                Il2CppFunctions.il2cpp_method_get_name(method)) ?? "";
            var kind = Classify(
                namespaze,
                typeName,
                methodName,
                ReadParameterTypes(method));
            if (kind == UnityObjectCallKind.None)
                return target;

            var cachedPointerField = Il2CppFunctions.il2cpp_class_get_field_from_name(
                declaringType,
                "m_CachedPtr");
            if (cachedPointerField == nint.Zero)
                return target;
            var cachedPointerOffset = Il2CppFunctions.il2cpp_field_get_offset(cachedPointerField);
            if (cachedPointerOffset < nint.Size * 2 || cachedPointerOffset > 4096)
                return target;

            var offset = checked((int)cachedPointerOffset);
            if (!RegisterMethod(kind, method, target, offset))
                return target;

            var stub = GetStubPointer(kind);
            if (stub == nint.Zero)
                return target;
            if (!RuntimeMethodCompatibility.RegisterPassThroughHandle(stub, target) &&
                (!RuntimeMethodCompatibility.TryResolveHandle(
                     stub,
                     out var registeredTarget,
                     out var compatibilityKind) ||
                 registeredTarget != target ||
                 compatibilityKind != RuntimeMethodCompatibilityKind.None))
            {
                return target;
            }
            return stub;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            OverflowException)
        {
            return target;
        }
    }

    internal static UnityObjectCallKind Classify(
        string namespaze,
        string typeName,
        string methodName,
        IReadOnlyList<string> parameterTypes)
    {
        if (!string.Equals(namespaze, "UnityEngine", StringComparison.Ordinal) ||
            !string.Equals(typeName, "Object", StringComparison.Ordinal))
        {
            return UnityObjectCallKind.None;
        }

        if (methodName == "Destroy")
        {
            if (Matches(parameterTypes, UnityObjectType))
                return UnityObjectCallKind.DestroyObject;
            if (Matches(parameterTypes, UnityObjectType, "System.Single"))
                return UnityObjectCallKind.DestroyObjectDelayed;
        }
        else if (methodName == "DestroyImmediate")
        {
            if (Matches(parameterTypes, UnityObjectType))
                return UnityObjectCallKind.DestroyImmediateObject;
            if (Matches(parameterTypes, UnityObjectType, "System.Boolean"))
                return UnityObjectCallKind.DestroyImmediateObjectAllowAssets;
        }
        else if (methodName == "DontDestroyOnLoad" &&
                 Matches(parameterTypes, UnityObjectType))
        {
            return UnityObjectCallKind.DontDestroyOnLoadObject;
        }
        else if (methodName == "op_Implicit" &&
                 Matches(parameterTypes, UnityObjectType))
        {
            return UnityObjectCallKind.ObjectImplicit;
        }

        return UnityObjectCallKind.None;
    }

    internal static bool IsCallableUnityObject(
        nint objectPointer,
        int cachedPointerOffset,
        IProcessMemoryReader reader)
    {
        if (objectPointer == nint.Zero ||
            cachedPointerOffset < nint.Size * 2 ||
            !IsAligned(objectPointer, nint.Size) ||
            !reader.TryReadPointer(objectPointer, out var klass) ||
            klass == nint.Zero ||
            !IsAligned(klass, nint.Size) ||
            !reader.IsReadable(klass, (nuint)nint.Size) ||
            !reader.TryReadPointer(objectPointer + cachedPointerOffset, out var nativeObject) ||
            nativeObject == nint.Zero ||
            !IsAligned(nativeObject, 4))
        {
            return false;
        }

        // Object::GetInstanceID reads the native object at +8 before a managed
        // exception can be raised, so validate that exact access range.
        return reader.IsReadable(nativeObject, 12);
    }

    internal static nint GetStubPointerForTesting(UnityObjectCallKind kind)
        => GetStubPointer(kind);

    private static bool Matches(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length)
            return false;
        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool IsAligned(nint value, int alignment)
        => ((nuint)value & (nuint)(alignment - 1)) == 0;

    private static string[] ReadParameterTypes(nint method)
    {
        var count = checked((int)Il2CppFunctions.il2cpp_method_get_param_count(method));
        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            var type = Il2CppFunctions.il2cpp_method_get_param(method, (uint)index);
            result[index] = type == nint.Zero
                ? ""
                : Marshal.PtrToStringAnsi(Il2CppFunctions.il2cpp_type_get_name(type)) ?? "";
        }
        return result;
    }

    private static bool RegisterMethod(
        UnityObjectCallKind kind,
        nint method,
        nint target,
        int cachedPointerOffset)
        => kind switch
        {
            UnityObjectCallKind.DestroyObject => Register(
                ref _destroyObject,
                new GuardedVoidObjectMethod(method, target, cachedPointerOffset)),
            UnityObjectCallKind.DestroyObjectDelayed => Register(
                ref _destroyObjectDelayed,
                new GuardedVoidObjectFloatMethod(method, target, cachedPointerOffset)),
            UnityObjectCallKind.DestroyImmediateObject => Register(
                ref _destroyImmediateObject,
                new GuardedVoidObjectMethod(method, target, cachedPointerOffset)),
            UnityObjectCallKind.DestroyImmediateObjectAllowAssets => Register(
                ref _destroyImmediateObjectAllowAssets,
                new GuardedVoidObjectBooleanMethod(method, target, cachedPointerOffset)),
            UnityObjectCallKind.DontDestroyOnLoadObject => Register(
                ref _dontDestroyOnLoadObject,
                new GuardedVoidObjectMethod(method, target, cachedPointerOffset)),
            UnityObjectCallKind.ObjectImplicit => Register(
                ref _objectImplicit,
                new GuardedBooleanObjectMethod(method, target, cachedPointerOffset)),
            _ => false,
        };

    private static bool Register<T>(ref T? location, T candidate)
        where T : GuardedMethod
    {
        var existing = Interlocked.CompareExchange(ref location, candidate, null);
        return existing == null || existing.Matches(candidate);
    }

    private static nint GetStubPointer(UnityObjectCallKind kind)
        => kind switch
        {
            UnityObjectCallKind.DestroyObject =>
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&DestroyObjectStub,
            UnityObjectCallKind.DestroyObjectDelayed =>
                (nint)(delegate* unmanaged[Cdecl]<nint, float, nint, void>)&DestroyObjectDelayedStub,
            UnityObjectCallKind.DestroyImmediateObject =>
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&DestroyImmediateObjectStub,
            UnityObjectCallKind.DestroyImmediateObjectAllowAssets =>
                (nint)(delegate* unmanaged[Cdecl]<nint, byte, nint, void>)&DestroyImmediateObjectAllowAssetsStub,
            UnityObjectCallKind.DontDestroyOnLoadObject =>
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&DontDestroyOnLoadObjectStub,
            UnityObjectCallKind.ObjectImplicit =>
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&ObjectImplicitStub,
            _ => nint.Zero,
        };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyObjectStub(nint instance, nint methodInfo)
        => Invoke(Volatile.Read(ref _destroyObject), instance);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyObjectDelayedStub(nint instance, float delay, nint methodInfo)
    {
        try
        {
            var method = Volatile.Read(ref _destroyObjectDelayed);
            if (method != null &&
                IsCallableUnityObject(instance, method.CachedPointerOffset, MemoryReader))
            {
                method.Original(instance, delay, method.Method);
            }
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyImmediateObjectStub(nint instance, nint methodInfo)
        => Invoke(Volatile.Read(ref _destroyImmediateObject), instance);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyImmediateObjectAllowAssetsStub(
        nint instance,
        byte allowAssets,
        nint methodInfo)
    {
        try
        {
            var method = Volatile.Read(ref _destroyImmediateObjectAllowAssets);
            if (method != null &&
                IsCallableUnityObject(instance, method.CachedPointerOffset, MemoryReader))
            {
                method.Original(instance, allowAssets, method.Method);
            }
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DontDestroyOnLoadObjectStub(nint instance, nint methodInfo)
        => Invoke(Volatile.Read(ref _dontDestroyOnLoadObject), instance);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ObjectImplicitStub(nint instance, nint methodInfo)
    {
        try
        {
            var method = Volatile.Read(ref _objectImplicit);
            if (method != null &&
                IsCallableUnityObject(instance, method.CachedPointerOffset, MemoryReader))
            {
                return method.Original(instance, method.Method);
            }
        }
        catch
        {
        }
        return 0;
    }

    private static void Invoke(GuardedVoidObjectMethod? method, nint instance)
    {
        try
        {
            if (method != null &&
                IsCallableUnityObject(instance, method.CachedPointerOffset, MemoryReader))
            {
                method.Original(instance, method.Method);
            }
        }
        catch
        {
        }
    }

    private abstract class GuardedMethod(nint method, nint target, int cachedPointerOffset)
    {
        internal nint Method { get; } = method;
        internal nint Target { get; } = target;
        internal int CachedPointerOffset { get; } = cachedPointerOffset;

        internal bool Matches(GuardedMethod other)
            => Method == other.Method &&
               Target == other.Target &&
               CachedPointerOffset == other.CachedPointerOffset;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidObjectDelegate(nint instance, nint methodInfo);

    private sealed class GuardedVoidObjectMethod(
        nint method,
        nint target,
        int cachedPointerOffset)
        : GuardedMethod(method, target, cachedPointerOffset)
    {
        internal VoidObjectDelegate Original { get; } =
            Marshal.GetDelegateForFunctionPointer<VoidObjectDelegate>(target);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidObjectFloatDelegate(nint instance, float delay, nint methodInfo);

    private sealed class GuardedVoidObjectFloatMethod(
        nint method,
        nint target,
        int cachedPointerOffset)
        : GuardedMethod(method, target, cachedPointerOffset)
    {
        internal VoidObjectFloatDelegate Original { get; } =
            Marshal.GetDelegateForFunctionPointer<VoidObjectFloatDelegate>(target);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidObjectBooleanDelegate(nint instance, byte allowAssets, nint methodInfo);

    private sealed class GuardedVoidObjectBooleanMethod(
        nint method,
        nint target,
        int cachedPointerOffset)
        : GuardedMethod(method, target, cachedPointerOffset)
    {
        internal VoidObjectBooleanDelegate Original { get; } =
            Marshal.GetDelegateForFunctionPointer<VoidObjectBooleanDelegate>(target);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BooleanObjectDelegate(nint instance, nint methodInfo);

    private sealed class GuardedBooleanObjectMethod(
        nint method,
        nint target,
        int cachedPointerOffset)
        : GuardedMethod(method, target, cachedPointerOffset)
    {
        internal BooleanObjectDelegate Original { get; } =
            Marshal.GetDelegateForFunctionPointer<BooleanObjectDelegate>(target);
    }

}

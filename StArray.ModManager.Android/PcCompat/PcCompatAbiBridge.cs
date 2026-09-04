namespace StArray.ModManager.Android.PcCompat;

using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Xphorror.PcModCompat;

public static class PcCompatAbiBridge
{
    private static readonly Func<Delegate, Type, object?> EventDelegateConverter = ConvertDelegate;
    private static readonly Func<object, Delegate?> EventSourceDelegateResolver = ResolveSourceDelegate;
    private static readonly Func<PcCompatManagedExecutionState, IDisposable?> EventCallbackScopeProvider =
        PcCompatRuntime.TryEnterManagedExternalCallbackScope;
    private static readonly ConcurrentDictionary<Type, MethodInfo> DelegateConverters = new();
    private static readonly MethodInfo ConvertDelegateDefinition = typeof(DelegateSupport)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(DelegateSupport.ConvertDelegate) &&
                          method.IsGenericMethodDefinition &&
                          method.GetGenericArguments().Length == 1);

    private static class NullableMethodCache<T> where T : struct
    {
        private static readonly IntPtr NullableClass = IL2CPP.RequireIl2CppClass(
            Il2CppClassPointerStore<Il2CppSystem.Nullable<T>>.NativeClassPtr,
            $"mscorlib.dll:System.Nullable`1<{typeof(T).FullName}>");

        internal static readonly IntPtr HasValue = IL2CPP.GetIl2CppMethodExact(
            NullableClass,
            isGeneric: false,
            isStatic: false,
            genericArity: 0,
            "get_HasValue",
            "System.Boolean");

        internal static readonly IntPtr Value = IL2CPP.GetIl2CppMethodExact(
            NullableClass,
            isGeneric: false,
            isStatic: false,
            genericArity: 0,
            "get_Value",
            IL2CPP.RenderTypeName<T>());
    }

    internal static void Install()
    {
        PcCompatManagedEventSubscriptionBridge.RegisterDelegateConverter(EventDelegateConverter);
        PcCompatManagedEventSubscriptionBridge.RegisterSourceDelegateResolver(
            EventSourceDelegateResolver);
        PcCompatManagedEventSubscriptionBridge.RegisterCallbackScopeProvider(
            EventCallbackScopeProvider);
    }

    public static object? BoxUnboxedValue<T>(Il2CppSystem.Object? source) where T : unmanaged
        => source is null ? null : source.Unbox<T>();

    public static Il2CppSystem.Nullable<T>? ToIl2CppNullable<T>(T? source) where T : struct
        // The generated setter receives a boxed native Nullable and unboxes it. A null reference is
        // therefore not the representation of Nullable<T>.None; use the generated parameterless
        // constructor so the native value carries HasValue=false.
        => source.HasValue
            ? new Il2CppSystem.Nullable<T>(source.Value)
            : new Il2CppSystem.Nullable<T>();

    /// <summary>
    /// Converts a generated boxed IL2CPP Nullable back to the CoreCLR Nullable used by a PC MOD.
    /// </summary>
    /// <remarks>
    /// The trimmed generated corlib proxy intentionally exposes only Nullable constructors. Calling
    /// the native <c>get_HasValue</c>/<c>get_Value</c> methods keeps this bridge independent of an
    /// accidentally omitted proxy member and avoids assuming that the two runtimes use identical
    /// struct padding.
    /// </remarks>
    public static unsafe T? ToManagedNullable<T>(Il2CppSystem.Nullable<T>? source) where T : struct
    {
        if (source is null)
            return null;

        var hasValueObject = InvokeNullableAccessor(
            source,
            NullableMethodCache<T>.HasValue,
            "System.Nullable<T>.get_HasValue");
        var hasValue = IL2CPP.PointerToValueGeneric<bool>(
            hasValueObject,
            isFieldPointer: false,
            valueTypeWouldBeBoxed: true);
        if (!hasValue)
            return null;

        var valueObject = InvokeNullableAccessor(
            source,
            NullableMethodCache<T>.Value,
            "System.Nullable<T>.get_Value");
        return IL2CPP.PointerToValueGeneric<T>(
            valueObject,
            isFieldPointer: false,
            valueTypeWouldBeBoxed: true);
    }

    private static unsafe IntPtr InvokeNullableAccessor<T>(
        Il2CppSystem.Nullable<T> source,
        IntPtr method,
        string identity)
        where T : struct
    {
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(
            method,
            source.Pointer,
            null,
            ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return IL2CPP.RequireIl2CppObject(result, identity);
    }

    public static TIl2Cpp? ToIl2CppDelegate<TIl2Cpp>(Delegate? source)
        where TIl2Cpp : Il2CppObjectBase
        => source is null ? null : DelegateSupport.ConvertDelegate<TIl2Cpp>(source);

    /// <summary>
    /// Converts a rewritten MOD delegate to the exact generated IL2CPP delegate type required by
    /// an event accessor. The event bridge calls this only on subscription, never in a hot
    /// callback path; DelegateSupport supplies the rooted Android wrapper and its own cache.
    /// </summary>
    private static object ConvertDelegate(Delegate source, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetType);
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(targetType))
        {
            throw new ArgumentException(
                $"Target delegate type is not an IL2CPP proxy: {targetType.FullName}",
                nameof(targetType));
        }

        var converter = DelegateConverters.GetOrAdd(
            targetType,
            static type => ConvertDelegateDefinition.MakeGenericMethod(type));
        try
        {
            return converter.Invoke(null, [source])
                   ?? throw new InvalidOperationException(
                       $"DelegateSupport returned null for {targetType.FullName}");
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is { } cause)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
            throw;
        }
    }

    private static Delegate? ResolveSourceDelegate(object source)
    {
        if (source is Delegate managed)
            return managed;
        return source is Il2CppObjectBase proxy &&
               DelegateSupport.TryResolveManagedDelegate(proxy, out var resolved)
            ? resolved
            : null;
    }

    private sealed class StringBuilderNativeContract
    {
        internal StringBuilderNativeContract(
            IntPtr classPointer,
            IntPtr stringConstructor,
            IntPtr parameterlessConstructor,
            IntPtr appendString)
        {
            ClassPointer = classPointer;
            StringConstructor = stringConstructor;
            ParameterlessConstructor = parameterlessConstructor;
            AppendString = appendString;
        }

        internal IntPtr ClassPointer { get; }
        internal IntPtr StringConstructor { get; }
        internal IntPtr ParameterlessConstructor { get; }
        internal IntPtr AppendString { get; }
    }

    private static readonly Lazy<StringBuilderNativeContract> StringBuilderContract =
        new(ResolveStringBuilderContract);

    /// <summary>
    /// Materializes a CoreCLR <see cref="System.Text.StringBuilder"/> as the Il2Cpp one the proxy
    /// signature requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the array wrappers, <c>Il2CppSystem.Text.StringBuilder</c> has no <c>op_Implicit</c>, so
    /// the rewriter cannot reach it without a helper here. The value is copied, not aliased: the two
    /// builders live in different heaps and there is no way to make them share storage.
    /// </para>
    /// <para>
    /// Copy semantics are correct for the audited callers. Every JipperOverlayer callsite is
    /// <c>text.SetText(sb)</c> - Unity reads the characters out during that call and keeps its own
    /// copy, so the builder is never retained on the Unity side and later MOD-side appends were never
    /// going to be observed anyway. A caller that expected Unity to hold a live reference would be
    /// broken by this, which is why it is stated rather than assumed.
    /// </para>
    /// <para>
    /// The runtime proxy is intentionally metadata-only and normally exposes only
    /// <c>.ctor(IntPtr)</c>. Therefore the text constructor is resolved from the native IL2CPP
    /// metadata and invoked with <c>il2cpp_runtime_invoke</c>; no generated proxy constructor or
    /// method wrapper is assumed. A small fallback uses the native parameterless constructor and
    /// native <c>Append(string)</c> when a target build has trimmed the string constructor.
    /// </para>
    /// </remarks>
    public static Il2CppSystem.Text.StringBuilder? ToIl2CppStringBuilder(
        System.Text.StringBuilder? source)
        => source is null ? null : MaterializeStringBuilder(source.ToString());

    private static StringBuilderNativeContract ResolveStringBuilderContract()
    {
        var classPointer = IL2CPP.RequireIl2CppClass(
            Il2CppClassPointerStore<Il2CppSystem.Text.StringBuilder>.NativeClassPtr,
            "mscorlib.dll:System.Text.StringBuilder");

        try
        {
            var stringConstructor = IL2CPP.GetIl2CppMethodExact(
                classPointer,
                isGeneric: false,
                isStatic: false,
                genericArity: 0,
                ".ctor",
                "System.Void",
                "System.String");
            return new StringBuilderNativeContract(
                classPointer,
                stringConstructor,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        catch (MissingMethodException)
        {
            // Some stripped Unity builds omit the string overload while retaining the ordinary
            // constructor and Append(string). Keep this compatibility fallback native as well.
            var parameterlessConstructor = IL2CPP.GetIl2CppMethodExact(
                classPointer,
                isGeneric: false,
                isStatic: false,
                genericArity: 0,
                ".ctor",
                "System.Void");
            var appendString = IL2CPP.GetIl2CppMethodExact(
                classPointer,
                isGeneric: false,
                isStatic: false,
                genericArity: 0,
                "Append",
                "System.Text.StringBuilder",
                "System.String");
            return new StringBuilderNativeContract(
                classPointer,
                IntPtr.Zero,
                parameterlessConstructor,
                appendString);
        }
    }

    private static unsafe Il2CppSystem.Text.StringBuilder MaterializeStringBuilder(string source)
    {
        var contract = StringBuilderContract.Value;
        var pointer = IL2CPP.RequireIl2CppObject(
            IL2CPP.il2cpp_object_new(contract.ClassPointer),
            "System.Text.StringBuilder allocation");
        var builder = new Il2CppSystem.Text.StringBuilder(pointer);

        var nativeSource = IL2CPP.ManagedStringToIl2Cpp(source);
        if (contract.StringConstructor != IntPtr.Zero)
        {
            InvokeNativeStringMethod(
                contract.StringConstructor,
                builder.Pointer,
                nativeSource,
                "System.Text.StringBuilder::.ctor(System.String)");
            return builder;
        }

        InvokeNativeMethod(
            contract.ParameterlessConstructor,
            builder.Pointer,
            parameters: null,
            "System.Text.StringBuilder::.ctor()");
        InvokeNativeStringMethod(
            contract.AppendString,
            builder.Pointer,
            nativeSource,
            "System.Text.StringBuilder::Append(System.String)");
        return builder;
    }

    private static unsafe void InvokeNativeStringMethod(
        IntPtr method,
        IntPtr instance,
        IntPtr nativeString,
        string identity)
    {
        var parameters = stackalloc void*[1];
        // il2cpp_runtime_invoke receives one pointer-sized slot per argument. For a reference
        // argument the slot contains the Il2CppObject* itself, matching the generated wrapper's
        // EmitObjectToPointer path for System.String.
        nativeString = IL2CPP.RequireIl2CppObject(nativeString, identity + " argument");
        parameters[0] = (void*)nativeString;
        InvokeNativeMethod(method, instance, parameters, identity);
    }

    private static unsafe void InvokeNativeMethod(
        IntPtr method,
        IntPtr instance,
        void** parameters,
        string identity)
    {
        var exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(
            IL2CPP.RequireIl2CppMethod(method, identity),
            IL2CPP.RequireIl2CppObject(instance, identity + " instance"),
            parameters,
            ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
    }
}

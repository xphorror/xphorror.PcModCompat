using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Xphorror.PcModCompat;

/// <summary>
/// Preserves nullable instance callbacks used by PC settings APIs. C# delegate
/// construction throws before the settings method can observe a null callback,
/// so rewritten callsites defer construction until this bridge sees the target.
/// </summary>
public static class PcCompatManagedSettingsDelegateBridge
{
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<DelegateKey, Action>>
        Cache = new();

    public static Action? CreateOptionalAction(
        object? target,
        RuntimeMethodHandle methodHandle,
        RuntimeTypeHandle declaringTypeHandle)
    {
        if (target is null)
            return null;

        var key = new DelegateKey(methodHandle.Value, declaringTypeHandle.Value);
        return Cache.GetValue(
                target,
                static _ => new ConcurrentDictionary<DelegateKey, Action>())
            .GetOrAdd(
                key,
                static (_, state) => CreateAction(
                    state.Target,
                    state.MethodHandle,
                    state.DeclaringTypeHandle),
                new DelegateFactoryState(target, methodHandle, declaringTypeHandle));
    }

    private static Action CreateAction(
        object target,
        RuntimeMethodHandle methodHandle,
        RuntimeTypeHandle declaringTypeHandle)
    {
        var method = MethodBase.GetMethodFromHandle(methodHandle, declaringTypeHandle) as MethodInfo
            ?? throw new MissingMethodException(
                "Optional settings callback method could not be resolved from its runtime handle.");
        return Delegate.CreateDelegate(typeof(Action), target, method, throwOnBindFailure: true) as Action
            ?? throw new InvalidOperationException(
                $"Optional settings callback {method.DeclaringType?.FullName}.{method.Name} is not an Action.");
    }

    private readonly record struct DelegateKey(IntPtr Method, IntPtr DeclaringType);

    private readonly record struct DelegateFactoryState(
        object Target,
        RuntimeMethodHandle MethodHandle,
        RuntimeTypeHandle DeclaringTypeHandle);
}

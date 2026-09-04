using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Xphorror.PcModCompat;

/// <summary>
/// Host-owned registry for MOD subscriptions to external static events.
/// </summary>
/// <remarks>
/// <para>
/// Rewritten PcCompat MOD assemblies route external static-event <c>add_</c> and <c>remove_</c>
/// accessors (today: <c>UnityEngine.Application.quitting</c>, <c>SceneManager.sceneUnloaded</c>,
/// <c>SceneManager.sceneLoaded</c>)
/// through <see cref="Subscribe"/> and <see cref="Unsubscribe"/>; the rewriter embeds the event
/// identity at the callsite via the spec's owner string. This is required because generated
/// IL2CPP proxies expose Il2CppSystem delegate parameters while the rewritten MOD keeps CoreCLR
/// delegates. The registry also guarantees that a session which faults, or is disabled without
/// its lifecycle running, does not leave a delegate pointing into a retired ALC behind on a
/// shared IL2CPP event.
/// </para>
/// <para>
/// This is the first slice of the static-event clause in the next-generation isolation
/// contract: subscriptions are recorded per <c>(modId, resource generation)</c> and retired
/// with the session. Instance events, events outside the generated proxy surface, and raw
/// <c>Delegate.Combine</c> remain unrewritten and stay a diagnosable isolation downgrade.
/// </para>
/// </remarks>
public static class PcCompatManagedEventSubscriptionBridge
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SessionKey, List<SubscriptionEntry>> Subscriptions = new();

    /// <summary>
    /// Resolved accessors per event key. Subscription happens on lifecycle paths (cold), so a
    /// plain locked dictionary is enough and reflection runs once per event identity.
    /// </summary>
    private static readonly Dictionary<string, (MethodInfo Add, MethodInfo Remove)> Accessors = new();

    // The Android host owns the actual CoreCLR -> IL2CPP conversion because the PcCompat
    // assembly intentionally has no Il2CppInterop dependency. Tests and non-Android hosts can
    // leave this unset as long as the handler is already assignable to the accessor parameter.
    private static Func<Delegate, Type, object?>? s_delegateConverter;
    private static Func<object, Delegate?>? s_sourceDelegateResolver;
    private static Func<PcCompatManagedExecutionState, IDisposable?>? s_callbackScopeProvider;

    // DelegateSupport roots the generated IL2CPP wrapper, but the bridge must also reuse the
    // exact wrapper for repeated add/remove operations. ConditionalWeakTable keeps this cache
    // from becoming a process-lifetime MOD delegate leak after a session is retired.
    private static ConditionalWeakTable<Delegate, ConvertedDelegateCache> s_convertedHandlers = new();
    private static ConditionalWeakTable<Delegate, ScopedDelegateCache> s_scopedHandlers = new();

    /// <summary>
    /// Replacement for an external static-event <c>add_</c> accessor.
    /// <paramref name="eventKey"/> is embedded at the callsite by the rewriter, in
    /// <c>&lt;assembly&gt;!&lt;type full name&gt;::&lt;event name&gt;</c> form.
    /// </summary>
    public static void Subscribe(object handler, string eventKey)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        var execution = RequireSession("MOD event subscription");
        var (add, remove) = RequireAccessors(eventKey, handler.GetType());
        var sourceHandler = RequireSourceDelegate(handler, eventKey);
        var scopedHandler = PrepareScopedHandler(sourceHandler, execution, eventKey);
        var accessorHandler = PrepareHandler(add, scopedHandler, eventKey);

        // Forward first so the MOD observes the same failure surface as the raw call; the
        // subscription is only recorded once the event actually accepted the handler.
        InvokeAccessor(add, accessorHandler, eventKey);

        lock (Gate)
        {
            var key = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
            if (!Subscriptions.TryGetValue(key, out var entries))
                Subscriptions[key] = entries = [];
            // Every forwarded add records its own entry, so retirement removes exactly what
            // this session added — including duplicate subscriptions of one handler
            // instance, which .NET multicast semantics keep as separate invocations.
            entries.Add(new SubscriptionEntry(eventKey, sourceHandler, accessorHandler, remove));
        }
    }

    /// <summary>
    /// Replacement for an external static-event <c>remove_</c> accessor. The remover is routed
    /// through the same conversion/cache path as <see cref="Subscribe"/> so a CoreCLR delegate
    /// can be removed from an IL2CPP event without an ABI mismatch. The matching registration is
    /// retired only after the native event accepted the removal.
    /// </summary>
    public static void Unsubscribe(object handler, string eventKey)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        var execution = RequireSession("MOD event unsubscription", allowDisable: true);
        var (_, remove) = RequireAccessors(eventKey, handler.GetType());
        var sourceHandler = RequireSourceDelegate(handler, eventKey);
        var tracked = FindLastSubscription(execution, eventKey, sourceHandler);
        // Reuse the exact wrapper recorded by add_ whenever this generation knows it. This
        // remains correct even if the MOD creates an equal-but-distinct managed delegate before
        // -=; only an untracked subscription needs a fresh conversion.
        var accessorHandler = tracked?.AccessorHandler ?? PrepareHandler(
            remove,
            PrepareScopedHandler(sourceHandler, execution, eventKey),
            eventKey);
        InvokeAccessor(remove, accessorHandler, eventKey);

        lock (Gate)
        {
            var key = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
            if (!Subscriptions.TryGetValue(key, out var entries))
                return;

            if (tracked != null)
            {
                for (var index = entries.Count - 1; index >= 0; index--)
                {
                    if (!ReferenceEquals(entries[index], tracked))
                        continue;
                    entries.RemoveAt(index);
                    if (entries.Count == 0)
                        Subscriptions.Remove(key);
                    return;
                }
            }

            // Event removal follows multicast delegate equality. Prefer the exact source object
            // and fall back to Delegate.Equals for a newly-created equivalent delegate.
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                if (!string.Equals(entry.EventKey, eventKey, StringComparison.Ordinal) ||
                    (!ReferenceEquals(entry.SourceHandler, sourceHandler) &&
                     !entry.SourceHandler.Equals(sourceHandler)))
                {
                    continue;
                }

                entries.RemoveAt(index);
                break;
            }
            if (entries.Count == 0)
                Subscriptions.Remove(key);
        }
    }

    private static SubscriptionEntry? FindLastSubscription(
        PcCompatManagedExecutionState execution,
        string eventKey,
        Delegate sourceHandler)
    {
        lock (Gate)
        {
            var key = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
            if (!Subscriptions.TryGetValue(key, out var entries))
                return null;
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                if (string.Equals(entry.EventKey, eventKey, StringComparison.Ordinal) &&
                    (ReferenceEquals(entry.SourceHandler, sourceHandler) ||
                     entry.SourceHandler.Equals(sourceHandler)))
                {
                    return entry;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Removes every subscription this generation recorded, by invoking the matching
    /// <c>remove_</c> accessor for each. Best-effort per entry: one failing removal must not
    /// strand the rest. Called when the session disables, after the MOD's own
    /// <c>OnDisable</c> had its chance to unsubscribe through the raw accessors.
    /// </summary>
    internal static void RetireOwner(string modId, long resourceSessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        SubscriptionEntry[] entries;
        lock (Gate)
        {
            var key = new SessionKey(modId, resourceSessionGeneration);
            if (!Subscriptions.Remove(key, out var list))
                return;
            entries = list.ToArray();
        }
        foreach (var entry in entries)
        {
            try
            {
                // Do not resolve or convert again here. The exact remover and wrapper used by
                // add_ are part of the entry so IL2CPP delegate identity remains stable.
                InvokeAccessor(entry.Remove, entry.AccessorHandler, entry.EventKey);
            }
            catch
            {
                // Best-effort retirement: the accessor may already be gone with a torn-down
                // proxy surface. A missed removal here is a leak, not a correctness hazard
                // for other MODs, and must not abort the rest of the teardown.
            }
        }
    }

    internal static void ClearAllForTests()
    {
        lock (Gate)
        {
            Subscriptions.Clear();
            Accessors.Clear();
            s_delegateConverter = null;
            s_sourceDelegateResolver = null;
            s_callbackScopeProvider = null;
            s_convertedHandlers = new ConditionalWeakTable<Delegate, ConvertedDelegateCache>();
            s_scopedHandlers = new ConditionalWeakTable<Delegate, ScopedDelegateCache>();
        }
    }

    /// <summary>
    /// Installs the host-owned converter for delegate types that cross from the rewritten
    /// managed MOD world into IL2CPP proxy accessors. Registration is idempotent for the same
    /// host and rejected when a different host tries to replace a live converter.
    /// </summary>
    internal static void RegisterDelegateConverter(Func<Delegate, Type, object?> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        lock (Gate)
        {
            if (s_delegateConverter is not null && !ReferenceEquals(s_delegateConverter, converter))
                throw new InvalidOperationException("PcCompat event delegate converter is already registered.");
            s_delegateConverter = converter;
        }
    }

    /// <summary>
    /// Installs the host-owned resolver that recovers the CoreCLR delegate rooted behind an
    /// Il2CppInterop generated delegate proxy.
    /// </summary>
    internal static void RegisterSourceDelegateResolver(Func<object, Delegate?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (Gate)
        {
            if (s_sourceDelegateResolver is not null &&
                !ReferenceEquals(s_sourceDelegateResolver, resolver))
            {
                throw new InvalidOperationException(
                    "PcCompat event source delegate resolver is already registered.");
            }
            s_sourceDelegateResolver = resolver;
        }
    }

    /// <summary>
    /// Registers the host callback lease provider used by external event wrappers. The provider
    /// must return a scope that restores owner/session/domain state, or null after retirement.
    /// </summary>
    internal static void RegisterCallbackScopeProvider(
        Func<PcCompatManagedExecutionState, IDisposable?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            if (s_callbackScopeProvider is not null &&
                !ReferenceEquals(s_callbackScopeProvider, provider))
            {
                throw new InvalidOperationException(
                    "PcCompat event callback scope provider is already registered.");
            }
            s_callbackScopeProvider = provider;
        }
    }

    private static PcCompatManagedExecutionState RequireSession(
        string operationDescription,
        bool allowDisable = false)
    {
        var execution = PcCompatManagedExecutionContext.Current
                        ?? throw new InvalidOperationException(
                            $"{operationDescription} requires an active managed scope.");
        if (!allowDisable && execution.Phase == PcCompatManagedExecutionPhase.Disable)
        {
            throw new InvalidOperationException(
                $"{operationDescription} is rejected while mod={execution.ModId} is disabling.");
        }
        return execution;
    }

    private static (MethodInfo Add, MethodInfo Remove) RequireAccessors(
        string eventKey,
        Type handlerType)
    {
        // Overloaded accessors resolve against the handler's runtime type, so the cache key
        // carries it; for the single-accessor events in the audited corpus the extra suffix
        // changes nothing.
        var cacheKey = eventKey + ":" + handlerType.FullName;
        lock (Gate)
        {
            if (Accessors.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var separator = eventKey.IndexOf("::", StringComparison.Ordinal);
        var bang = eventKey.IndexOf('!');
        if (bang <= 0 || separator <= bang)
            throw new InvalidOperationException(
                $"MOD event subscription key must be '<assembly>!<type>::<event>': {eventKey}");
        var assemblyName = eventKey[..bang];
        var typeName = eventKey[(bang + 1)..separator];
        var eventName = eventKey[(separator + 2)..];

        var type = FindType(assemblyName, typeName)
                   ?? throw new InvalidOperationException(
                       $"MOD event subscription target type was not found: {eventKey}");
        var add = FindAccessor(type, "add_" + eventName, handlerType)
                  ?? throw new InvalidOperationException(
                      $"MOD event subscription target has no resolvable accessor: {eventKey}");
        var remove = FindAccessor(type, "remove_" + eventName, handlerType)
                     ?? throw new InvalidOperationException(
                         $"MOD event subscription target has no resolvable remover: {eventKey}");

        lock (Gate)
        {
            // Another thread may have cached while this one resolved; the resolution is
            // deterministic, so either entry is correct.
            return Accessors[cacheKey] = (add, remove);
        }
    }

    private static Type? FindType(string assemblyName, string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type != null)
                return type;
        }
        try
        {
            var loaded = Assembly.Load(assemblyName);
            return loaded.GetType(typeName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? FindAccessor(Type type, string accessorName, Type handlerType)
    {
        // Static events only: the audited corpus subscribes to static Unity events, and
        // instance-event receivers are not representable in the callsite contract yet.
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == accessorName && method.GetParameters().Length == 1)
            .ToArray();
        if (candidates.Length == 0)
            return null;
        if (candidates.Length == 1)
            return candidates[0];
        // Multiple overloads: exact runtime-type match first, then the single assignable
        // candidate; anything else stays fail-closed instead of guessing.
        var exact = candidates.FirstOrDefault(method =>
            method.GetParameters()[0].ParameterType == handlerType);
        if (exact != null)
            return exact;
        var assignable = candidates.Where(method =>
            method.GetParameters()[0].ParameterType.IsAssignableFrom(handlerType)).ToArray();
        return assignable.Length == 1 ? assignable[0] : null;
    }

    private static void InvokeAccessor(MethodInfo accessor, object handler, string eventKey)
    {
        try
        {
            accessor.Invoke(null, [handler]);
        }
        catch (TargetInvocationException invocation)
        {
            if (invocation.InnerException is { } cause)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
            throw;
        }
    }

    private static object PrepareHandler(MethodInfo accessor, object handler, string eventKey)
    {
        var targetType = accessor.GetParameters()[0].ParameterType;
        if (targetType.IsInstanceOfType(handler))
            return handler;

        if (handler is not Delegate source)
        {
            throw new ArgumentException(
                $"MOD event handler is not a delegate for {eventKey}: " +
                $"source={handler.GetType().FullName}, target={targetType.FullName}");
        }

        ConvertedDelegateCache cache;
        Func<Delegate, Type, object?> converter;
        lock (Gate)
        {
            converter = s_delegateConverter
                ?? throw new InvalidOperationException(
                    $"No delegate converter is registered for MOD event {eventKey}: " +
                    $"source={source.GetType().FullName}, target={targetType.FullName}");
            if (!s_convertedHandlers.TryGetValue(source, out var existing))
            {
                cache = new ConvertedDelegateCache();
                s_convertedHandlers.Add(source, cache);
            }
            else
            {
                cache = existing;
            }

            if (cache.Values.TryGetValue(targetType, out var cached))
                return ValidateConvertedHandler(cached, targetType, eventKey);

            // Conversion is cold-path and remains under the registry lock so concurrent
            // duplicate subscriptions cannot create different wrappers for one source/type.
            var converted = converter(source, targetType)
                ?? throw new InvalidOperationException(
                    $"Delegate converter returned null for MOD event {eventKey}: " +
                    $"source={source.GetType().FullName}, target={targetType.FullName}");
            cache.Values[targetType] = converted;
            return ValidateConvertedHandler(converted, targetType, eventKey);
        }
    }

    private static Delegate RequireSourceDelegate(object handler, string eventKey)
    {
        if (handler is Delegate source)
            return source;

        Func<object, Delegate?> resolver;
        lock (Gate)
        {
            resolver = s_sourceDelegateResolver
                ?? throw new InvalidOperationException(
                    $"No source delegate resolver is registered for MOD event {eventKey}: " +
                    $"source={handler.GetType().FullName}");
        }

        return resolver(handler)
               ?? throw new InvalidOperationException(
                   $"MOD event handler does not contain a rooted managed delegate for {eventKey}: " +
                   $"source={handler.GetType().FullName}");
    }

    private static Delegate PrepareScopedHandler(
        Delegate source,
        PcCompatManagedExecutionState execution,
        string eventKey)
    {
        Func<PcCompatManagedExecutionState, IDisposable?> scopeProvider;
        var owner = execution.Phase == PcCompatManagedExecutionPhase.Update
            ? execution
            : execution with { Phase = PcCompatManagedExecutionPhase.Update };
        var key = new SessionKey(owner.ModId, owner.ResourceSessionGeneration);
        lock (Gate)
        {
            scopeProvider = s_callbackScopeProvider
                ?? throw new InvalidOperationException(
                    $"No callback scope provider is registered for MOD event {eventKey}.");
            if (!s_scopedHandlers.TryGetValue(source, out var cache))
            {
                cache = new ScopedDelegateCache();
                s_scopedHandlers.Add(source, cache);
            }
            if (cache.Values.TryGetValue(key, out var cached))
                return cached;

            var wrapped = CompileScopedHandler(source, owner, scopeProvider, eventKey);
            cache.Values.Add(key, wrapped);
            return wrapped;
        }
    }

    private static Delegate CompileScopedHandler(
        Delegate source,
        PcCompatManagedExecutionState owner,
        Func<PcCompatManagedExecutionState, IDisposable?> scopeProvider,
        string eventKey)
    {
        var delegateType = source.GetType();
        var invoke = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new InvalidOperationException(
                         $"MOD event handler has no Invoke method: {eventKey}");
        var parameters = invoke.GetParameters();
        if (parameters.Any(parameter => parameter.ParameterType.IsByRef))
        {
            throw new InvalidOperationException(
                $"MOD event handlers with by-reference parameters are unsupported: {eventKey}");
        }

        var arguments = parameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var callbackScope = Expression.Variable(typeof(IDisposable), "callbackScope");
        var enterScope = Expression.Assign(
            callbackScope,
            Expression.Invoke(
                Expression.Constant(scopeProvider),
                Expression.Constant(owner)));
        var hasScope = Expression.NotEqual(
            callbackScope,
            Expression.Constant(null, typeof(IDisposable)));
        var disposeScope = Expression.Call(
            callbackScope,
            typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!);
        var invokeSource = Expression.Invoke(
            Expression.Constant(source, delegateType),
            arguments);

        Expression body;
        if (invoke.ReturnType == typeof(void))
        {
            body = Expression.Block(
                [callbackScope],
                enterScope,
                Expression.IfThen(
                    hasScope,
                    Expression.TryFinally(invokeSource, disposeScope)));
        }
        else
        {
            var result = Expression.Variable(invoke.ReturnType, "result");
            body = Expression.Block(
                [callbackScope, result],
                enterScope,
                Expression.Assign(result, Expression.Default(invoke.ReturnType)),
                Expression.IfThen(
                    hasScope,
                    Expression.TryFinally(
                        Expression.Assign(result, invokeSource),
                        disposeScope)),
                result);
        }

        // Event subscription is a cold path. Compile once and cache the direct-call wrapper;
        // the callback path allocates no object[] and does not use DynamicInvoke/reflection.
        return Expression.Lambda(delegateType, body, arguments).Compile();
    }

    private static object ValidateConvertedHandler(object converted, Type targetType, string eventKey)
    {
        if (!targetType.IsInstanceOfType(converted))
        {
            throw new InvalidOperationException(
                $"Delegate converter returned incompatible type for MOD event {eventKey}: " +
                $"actual={converted.GetType().FullName}, target={targetType.FullName}");
        }
        return converted;
    }

    private readonly record struct SessionKey(string ModId, long Generation);

    private sealed record SubscriptionEntry(
        string EventKey,
        Delegate SourceHandler,
        object AccessorHandler,
        MethodInfo Remove);

    private sealed class ConvertedDelegateCache
    {
        public Dictionary<Type, object> Values { get; } = new();
    }

    private sealed class ScopedDelegateCache
    {
        public Dictionary<SessionKey, Delegate> Values { get; } = new();
    }
}

using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace Xphorror.PcModCompat;

public sealed record PcCompatManagedComponentOwnerSnapshot(
    long Identity,
    object GameObject,
    bool IsAlive,
    bool IsActive);

public sealed record PcCompatUnityObjectLeaseSnapshot(
    long OwnerIdentity,
    string Kind,
    string TypeName,
    bool Destroying,
    bool DestroyScheduled);

public enum PcCompatManagedYieldDelayKind
{
    ScaledSeconds,
    RealtimeSeconds
}

public readonly record struct PcCompatManagedYieldDelay(
    PcCompatManagedYieldDelayKind Kind,
    float Seconds);

public sealed record PcCompatManagedComponentHostOperations(
    Func<object, object> ResolveTransform,
    Func<Type, bool> IsNativeComponentType,
    Func<Type, string, bool> IsManagedComponentTypeOwnedByMod,
    Func<object, Type, object> AddNativeComponent,
    Func<object, Type, object?> GetNativeComponent,
    Func<object, Type, IReadOnlyList<object>> GetNativeComponents,
    Func<object, bool> ReadNativeBehaviourEnabled,
    Action<object, bool> WriteNativeBehaviourEnabled,
    Func<object, bool> IsGameObject,
    Func<object, bool> IsNativeObjectAlive,
    Action<object> DontDestroyNativeObject,
    Action<object> DestroyNativeObject,
    Action<object, float> DestroyNativeObjectDelayed,
    Func<object, PcCompatManagedYieldDelay?> ResolveYieldDelay,
    Func<float> ReadScaledDeltaTime,
    Func<string, object> CreateNativeGameObject,
    Func<object, object?, object> InstantiateNativeObject,
    // Boxed-proxy accessors for the shared RectTransform.anchoredPosition property. The value is a
    // boxed generated-proxy UnityEngine.Vector2: neither this assembly nor the host can name that
    // type statically, and the contribution registry only ever needs to store it and hand it back.
    Func<object, object> ReadNativeAnchoredPosition,
    Action<object, object> WriteNativeAnchoredPosition,
    // Render-component support. Kept as host callbacks for the same reason as everything above: the
    // proxy types are resolved at runtime and cannot be named from this assembly.
    //
    // BindManagedRenderComponent takes the MOD component type and the host component instance, and
    // returns a managed instance of that type whose Il2Cpp pointer is the host's. It must not run the
    // proxy base constructor on that pointer - the rewriter blanks the MOD-side base call for exactly
    // that reason.
    Func<Type, object, object> BindManagedRenderComponent,
    // The Il2Cpp object address of a bound instance, used as the native prefilter key.
    Func<object, long> ReadNativeInstancePointer,
    // Wraps a raw Il2Cpp pointer as a requested generated proxy. This is used both for render
    // callback parameters and for Unity Type-based component APIs whose declared Component return
    // type erases the concrete proxy type requested by the caller.
    Func<Type, nint, object> WrapNativeProxyPointer,
    // Owner-aware GameObject activation. The generated GameObject proxy is intentionally not
    // called by MOD code directly: the bridge first validates the current session and native
    // identity, then forwards this operation to the host.
    Action<object, bool> SetNativeGameObjectActive,
    Action<string, long> RegisterNativeRenderHost,
    Action<string, long> UnregisterNativeRenderHost,
    Action<string> ClearNativeRenderHosts,
    Func<object?, object?, bool> AreNativeObjectsEqual);

public sealed class PcCompatManagedComponentLifecycleSnapshot
{
    public long FrameGeneration { get; init; }
    public IReadOnlyList<PcCompatManagedComponentLifecycleEntry> Components { get; init; } =
        Array.Empty<PcCompatManagedComponentLifecycleEntry>();
}

public sealed class PcCompatManagedComponentLifecycleEntry
{
    public required string TypeName { get; init; }
    public bool Active { get; init; }
    public bool Started { get; init; }
    public bool Destroying { get; init; }
    public long AwakeCount { get; init; }
    public long OnEnableCount { get; init; }
    public long StartCount { get; init; }
    public long UpdateCount { get; init; }
    public long LateUpdateCount { get; init; }
    public long OnDisableCount { get; init; }
    public long OnDestroyCount { get; init; }
    public long OnGuiCount { get; init; }
}

/// <summary>
/// Hosts MOD-local MonoBehaviour subclasses without registering them in the
/// IL2CPP class table. All mutations and lifecycle callbacks are UnityMain-only.
/// </summary>
public static class PcCompatManagedComponentBridge
{
    private const string UnloadDebugTag = "[DEBUG-kv-unload-v1]";
    private static readonly object Gate = new();
    private static readonly SessionKeyComparer SessionKeys = new();
    private static readonly Dictionary<SessionKey, SessionBucket> Sessions =
        new(SessionKeys);
    private static Dictionary<SessionKey, SessionDispatchState> s_dispatchStates =
        new(SessionKeys);
    private static readonly Dictionary<object, ComponentEntry> Components =
        new(ReferenceEqualityComparer.Instance);
    private static readonly ConditionalWeakTable<object, ManagedComponentObjectState>
        ManagedComponentObjectStates = new();
    private static readonly Dictionary<object, NativeObjectLease> NativeLeases =
        new(ReferenceEqualityComparer.Instance);
    // Unity can manufacture more than one managed proxy for the same native object. Wrapper
    // reference is retained for fast-path bookkeeping, while this index keeps ownership and
    // destroy routing stable across proxy rewrapping.
    private static readonly Dictionary<long, NativeObjectLease> NativeLeasesByIdentity = [];
    private static readonly Dictionary<SharedPropertyKey, SharedPropertyState>
        SharedProperties = new();
    /// <summary>
    /// Bound render components, keyed by the Il2Cpp address of their host component. The render hook
    /// arrives with nothing but that address, so this is what turns it back into a managed instance.
    /// </summary>
    /// <remarks>
    /// A plain long key, not the host proxy object: two proxy wrappers of the same native object are
    /// different references, and the hook does not hand us either of them.
    /// </remarks>
    private static readonly Dictionary<long, RenderComponentBinding> RenderComponents = new();
    private static long s_nextSharedPropertyContributionSequence;
    private static Func<object, PcCompatManagedComponentOwnerSnapshot>? s_ownerResolver;
    private static PcCompatManagedComponentHostOperations? s_hostOperations;
    private static Func<string, string, Type?>? s_renderProxyTypeResolver;
    private static Action? s_demandChanged;

    /// <summary>
    /// Resolves a generated proxy type by assembly and full name, for the render-component host
    /// lookup. Separate from <see cref="PcCompatManagedComponentHostOperations"/> because the proxy
    /// registry is a host concern with no per-session state.
    /// </summary>
    public static void RegisterRenderProxyTypeResolver(Func<string, string, Type?>? resolver)
        => Volatile.Write(ref s_renderProxyTypeResolver, resolver);

    public static void RegisterOwnerResolver(
        Func<object, PcCompatManagedComponentOwnerSnapshot>? resolver)
    {
        lock (Gate)
        {
            if (Sessions.Count != 0 &&
                !ReferenceEquals(s_ownerResolver, resolver))
            {
                throw new InvalidOperationException(
                    "Managed component owner resolver cannot change while components are registered.");
            }
            Volatile.Write(ref s_ownerResolver, resolver);
        }
    }

    public static void RegisterHostOperations(
        PcCompatManagedComponentHostOperations? operations)
    {
        lock (Gate)
        {
            if (Sessions.Count != 0 &&
                !ReferenceEquals(s_hostOperations, operations))
            {
                throw new InvalidOperationException(
                    "Managed component host operations cannot change while components are registered.");
            }
            Volatile.Write(ref s_hostOperations, operations);
        }
    }

    public static void RegisterDemandChangedSink(Action? sink)
        => Volatile.Write(ref s_demandChanged, sink);

    public static T AddComponent<T>(object owner)
        => (T)AddManagedComponent(owner, typeof(T));

    /// <summary>
    /// Replaces Unity object operators when an operand can be a MOD-local managed component.
    /// Those components have no IL2CPP identity, so Unity's native operator sees every live
    /// instance as fake-null.
    /// </summary>
    public static bool ObjectEquals(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        var leftManaged = TryGetManagedComponentObjectState(left, out var leftState);
        var rightManaged = TryGetManagedComponentObjectState(right, out var rightState);
        if (leftManaged || rightManaged)
        {
            var leftNullLike = left is null || (leftManaged
                ? !leftState!.IsAlive
                : !RequireHostOperations().IsNativeObjectAlive(left));
            var rightNullLike = right is null || (rightManaged
                ? !rightState!.IsAlive
                : !RequireHostOperations().IsNativeObjectAlive(right));
            return leftNullLike && rightNullLike;
        }

        return RequireHostOperations().AreNativeObjectsEqual(left, right);
    }

    public static bool ObjectNotEquals(object? left, object? right)
        => !ObjectEquals(left, right);

    public static bool ObjectImplicit(object? value)
    {
        if (value is null)
            return false;
        if (TryGetManagedComponentObjectState(value, out var state))
            return state!.IsAlive;
        return RequireHostOperations().IsNativeObjectAlive(value);
    }

    /// <summary>
    /// Replacement for <c>new GameObject(name)</c>. The object is registered to the calling
    /// MOD session before it is handed back, so it participates in the owner audit snapshot,
    /// cross-MOD destroy rejection and session teardown.
    /// </summary>
    /// <remarks>
    /// Without this, a MOD-created host GameObject had no lease while <c>Destroy</c> already
    /// checked ownership — objects were created unowned but destroyed under owner rules, so the
    /// create/destroy loop was open on the create side.
    /// </remarks>
    public static object CreateGameObject(string name)
    {
        RequireUnityMain();
        var execution = RequireCanAdd();
        var operations = RequireHostOperations();
        var gameObject = operations.CreateNativeGameObject(name ?? string.Empty)
                         ?? throw new InvalidOperationException(
                             "Native GameObject creation returned null.");
        RegisterCreatedObject(execution, operations, gameObject);
        return gameObject;
    }

    /// <summary>Replacement for <c>Object.Instantiate(original)</c>.</summary>
    public static object Instantiate(object original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return InstantiateCore(original, null);
    }

    /// <summary>Replacement for <c>Object.Instantiate(original, parent)</c>.</summary>
    public static object Instantiate(object original, object? parent)
    {
        ArgumentNullException.ThrowIfNull(original);
        return InstantiateCore(original, parent);
    }

    private static object InstantiateCore(object original, object? parent)
    {
        RequireUnityMain();
        var execution = RequireCanAdd();
        var operations = RequireHostOperations();
        // The prototype keeps its own ownership; only the clone becomes MOD-owned.
        var clone = operations.InstantiateNativeObject(original, parent)
                    ?? throw new InvalidOperationException(
                        "Native Object.Instantiate returned null.");
        RegisterCreatedObject(execution, operations, clone);
        return clone;
    }

    /// <summary>
    /// Registers a freshly created Unity object as owned by the current session. On failure the
    /// object is destroyed rather than leaked as an unowned Unity object.
    /// </summary>
    private static void RegisterCreatedObject(
        PcCompatManagedExecutionState execution,
        PcCompatManagedComponentHostOperations operations,
        object created)
    {
        try
        {
            var snapshot = ResolveOwner(created);
            if (!snapshot.IsAlive || snapshot.Identity == 0)
            {
                throw new InvalidOperationException(
                    "Created Unity object is not alive immediately after creation.");
            }
            RegisterNativeLease(
                new SessionKey(execution.ModId, execution.ResourceSessionGeneration),
                snapshot,
                created,
                created.GetType());
        }
        catch (Exception exception)
        {
            var failures = new List<string>
            {
                "Created Unity object ownership registration failed: " + exception
            };
            DestroyNativeObject(operations, created, failures);
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    public static object AddComponent(object owner, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(componentType);
        RequireUnityMain();
        var execution = RequireCanAdd();
        var operations = RequireHostOperations();
        if (!operations.IsNativeComponentType(componentType))
            return AddManagedComponent(owner, componentType);

        var ownerSnapshot = ResolveOwner(owner);
        if (!ownerSnapshot.IsAlive || ownerSnapshot.Identity == 0)
        {
            throw new InvalidOperationException(
                $"Cannot add native component {componentType.FullName} to a destroyed owner.");
        }
        var rawComponent = operations.AddNativeComponent(ownerSnapshot.GameObject, componentType)
                           ?? throw new InvalidOperationException(
                               $"Native AddComponent returned null for {componentType.AssemblyQualifiedName}.");
        var component = NormalizeNativeComponentResult(
            operations,
            rawComponent,
            componentType,
            "AddComponent")!;
        try
        {
            RegisterNativeLease(
                new SessionKey(execution.ModId, execution.ResourceSessionGeneration),
                ownerSnapshot,
                component,
                componentType);
        }
        catch (Exception exception)
        {
            var failures = new List<string> { "Native Unity object lease registration failed: " + exception };
            DestroyNativeObject(operations, component, failures);
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
        return component;
    }

    private static object AddManagedComponent(object owner, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireUnityMain();
        var execution = RequireCanAdd();
        if (PcCompatManagedRenderComponentCatalog.TryMatchRuntimeType(
                execution.ModId,
                componentType,
                out var renderEntry))
        {
            return AddManagedRenderComponent(owner, componentType, execution, renderEntry);
        }
        ValidateManagedComponentType(componentType);
        if (!RequireHostOperations().IsManagedComponentTypeOwnedByMod(
                componentType,
                execution.ModId))
        {
            throw new InvalidOperationException(
                $"Managed component type is not owned by MOD '{execution.ModId}': " +
                componentType.AssemblyQualifiedName);
        }
        var ownerSnapshot = ResolveOwner(owner);
        if (!ownerSnapshot.IsAlive || ownerSnapshot.Identity == 0)
        {
            throw new InvalidOperationException(
                $"Cannot add managed component {componentType.FullName} to a destroyed owner.");
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(componentType, nonPublic: true)
                       ?? throw new InvalidOperationException(
                           $"Constructor returned null for managed component {componentType.FullName}.");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not construct managed component {componentType.FullName}.",
                Unwrap(exception));
        }

        var entry = ComponentEntry.Create(
            new SessionKey(execution.ModId, execution.ResourceSessionGeneration),
            ownerSnapshot,
            instance,
            componentType);
        Register(entry);
        try
        {
            Invoke(entry, entry.Awake, "Awake");
            if (!IsRegistered(entry))
                return instance;
            if (ownerSnapshot.IsActive && entry.ReadEnabled())
            {
                entry.Active = true;
                Invoke(entry, entry.OnEnable, "OnEnable");
            }
        }
        catch (Exception exception)
        {
            var cleanupError = DestroyEntry(entry);
            throw new InvalidOperationException(
                FormatFailure(entry, "initialization", exception, cleanupError),
                Unwrap(exception));
        }

        return instance;
    }

    /// <summary>
    /// Adds a registered render component: a real host proxy component goes on the GameObject, the
    /// MOD's managed type is bound to that host's Il2Cpp instance, and the pair is registered so the
    /// native render hook can find its way back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two registrations, not one. The managed shell goes in <c>Components</c> as an ordinary
    /// <c>ComponentEntry</c> so it inherits owner checks, the audit snapshot and session teardown; the
    /// host component goes in <c>NativeLeases</c> so <c>Object.Destroy</c> already refuses to let
    /// another MOD destroy it. The alternative - one purpose-built table - would have to reimplement
    /// teardown, and JipperKeyViewer's rain pool destroys objects past its 64-drop ceiling, so a leak
    /// there would be continuous rather than one-off.
    /// </para>
    /// <para>
    /// Lifecycle messages still bind through <c>ComponentEntry</c>, which walks base types looking for
    /// Awake/Update/etc. For a type deriving a proxy those base types are proxy classes, and
    /// <c>IsModManagedImplementation</c> already refuses to bind anything a proxy declares - so a
    /// proxy's own method can never be mistaken for a MOD lifecycle message. <c>RainGraphic</c>
    /// declares none of them anyway.
    /// </para>
    /// </remarks>
    private static object AddManagedRenderComponent(
        object owner,
        Type componentType,
        PcCompatManagedExecutionState execution,
        PcCompatManagedRenderComponentCatalog.Entry catalogEntry)
    {
        ValidateManagedRenderComponentType(componentType, catalogEntry);
        var operations = RequireHostOperations();
        if (!operations.IsManagedComponentTypeOwnedByMod(componentType, execution.ModId))
        {
            throw new InvalidOperationException(
                $"Managed render component type is not owned by MOD '{execution.ModId}': " +
                componentType.AssemblyQualifiedName);
        }

        var ownerSnapshot = ResolveOwner(owner);
        if (!ownerSnapshot.IsAlive || ownerSnapshot.Identity == 0)
        {
            throw new InvalidOperationException(
                $"Cannot add managed render component {componentType.FullName} to a destroyed owner.");
        }

        var hostType = ResolveRenderHostType(catalogEntry);
        var host = operations.AddNativeComponent(ownerSnapshot.GameObject, hostType)
                   ?? throw new InvalidOperationException(
                       $"Native AddComponent returned null for host {catalogEntry.HostType}.");

        var session = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        var hostLeased = false;
        ComponentEntry? entry = null;
        var renderKey = 0L;
        try
        {
            RegisterNativeLease(session, ownerSnapshot, host, hostType);
            hostLeased = true;

            var instance = operations.BindManagedRenderComponent(componentType, host)
                           ?? throw new InvalidOperationException(
                               $"Binding {componentType.FullName} to host {catalogEntry.HostType} " +
                               "returned null.");
            renderKey = operations.ReadNativeInstancePointer(instance);
            if (renderKey == 0)
            {
                throw new InvalidOperationException(
                    $"Bound render component {componentType.FullName} has no native instance pointer.");
            }

            entry = ComponentEntry.Create(session, ownerSnapshot, instance, componentType);
            Register(entry);

            var renderMethod = ResolveRenderMethod(componentType, catalogEntry);
            var parameterType = renderMethod.GetParameters()[0].ParameterType;
            var binding = new RenderComponentBinding(
                session,
                entry,
                host,
                renderKey,
                renderMethod,
                parameterType);
            lock (Gate)
            {
                if (RenderComponents.TryGetValue(renderKey, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Render host 0x{renderKey:X} is already bound to " +
                        existing.Entry.ComponentType.FullName + ".");
                }
                RenderComponents.Add(renderKey, binding);
            }

            // Last, and only after every managed table is consistent: the moment this returns, the
            // native hook can dispatch on this pointer.
            operations.RegisterNativeRenderHost(session.ModId, renderKey);
            return instance;
        }
        catch (Exception exception)
        {
            var failures = new List<string>
            {
                $"Managed render component {componentType.FullName} registration failed: " + exception
            };
            if (renderKey != 0)
            {
                try
                {
                    operations.UnregisterNativeRenderHost(session.ModId, renderKey);
                }
                catch (Exception cleanup)
                {
                    failures.Add("native render host unregister failed: " + cleanup);
                }
                lock (Gate)
                    RenderComponents.Remove(renderKey);
            }
            if (entry != null)
                AddFailure(failures, DestroyEntry(entry));
            if (hostLeased && TryGetNativeLease(host, out var lease))
                AddFailure(failures, DestroyNativeLease(lease, operations));
            else if (!hostLeased)
                DestroyNativeObject(operations, host, failures);
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, failures),
                Unwrap(exception));
        }
    }

    /// <summary>
    /// Forwards a host render callback to its bound managed component. Called from the native
    /// synchronous-prefix path on UnityMain.
    /// </summary>
    /// <returns>
    /// True when a bound component consumed the callback, so the host's own mesh build must be
    /// skipped. False leaves the original to run - which is the correct answer for an unbound
    /// instance, a retired session, or a callback that threw.
    /// </returns>
    /// <remarks>
    /// A throwing MOD override returns false rather than propagating: the caller is a native Unity
    /// render callback, and letting the exception cross that boundary would take the process down.
    /// The host then draws its own quad, which for a bound rain drop is a visible artifact - but a
    /// visible artifact is recoverable and a crash is not.
    /// </remarks>
    public static bool TryDispatchRenderCallback(long hostInstancePointer, nint argumentPointer)
    {
        RenderComponentBinding? binding;
        lock (Gate)
        {
            if (!RenderComponents.TryGetValue(hostInstancePointer, out binding))
                return false;
        }
        if (binding.Entry.Destroying || !IsRegistered(binding.Entry))
            return false;

        var operations = Volatile.Read(ref s_hostOperations);
        if (operations is null)
            return false;

        try
        {
            var argument = argumentPointer == 0
                ? null
                : operations.WrapNativeProxyPointer(binding.ParameterType, argumentPointer);
            using var execution = PcCompatManagedExecutionContext.Enter(
                new PcCompatManagedExecutionState(
                    binding.Session.ModId,
                    binding.Session.Generation,
                    PcCompatManagedExecutionPhase.Update));
            binding.Invoke(argument);
            return true;
        }
        catch (Exception exception)
        {
            binding.NoteFailure();
            Logger.Error(
                nameof(PcCompatManagedComponentBridge),
                $"render callback failed mod={binding.Session.ModId} " +
                $"type={binding.Entry.ComponentType.FullName} " +
                $"failures={binding.FailureCount}: {Unwrap(exception).Message}");
            return false;
        }
    }

    /// <summary>Diagnostics counter: how many render hosts this session currently has bound.</summary>
    public static int CountRenderComponents(string modId, long sessionGeneration)
    {
        lock (Gate)
        {
            return RenderComponents.Values.Count(binding =>
                StringComparer.OrdinalIgnoreCase.Equals(binding.Session.ModId, modId) &&
                binding.Session.Generation == sessionGeneration);
        }
    }

    private static void ValidateManagedRenderComponentType(
        Type componentType,
        PcCompatManagedRenderComponentCatalog.Entry catalogEntry)
    {
        if (componentType.IsAbstract || componentType.ContainsGenericParameters)
        {
            throw new InvalidOperationException(
                "Managed render component type must be closed and concrete: " +
                componentType.AssemblyQualifiedName);
        }
        // The declared base is re-checked here as well as in the rewriter, because the two run at
        // different times against different inputs: the rewriter reads the MOD assembly on disk, and
        // this reads the type actually loaded. A proxy regeneration that changes the hierarchy between
        // them would otherwise bind a managed shell to a host it does not derive.
        var baseType = componentType.BaseType;
        if (baseType?.FullName != catalogEntry.BaseType)
        {
            throw new InvalidOperationException(
                $"Managed render component {componentType.FullName} derives " +
                $"{baseType?.FullName ?? "<none>"}, but its registration declares " +
                catalogEntry.BaseType);
        }
    }

    private static Type ResolveRenderHostType(
        PcCompatManagedRenderComponentCatalog.Entry catalogEntry)
    {
        var resolver = Volatile.Read(ref s_renderProxyTypeResolver)
                       ?? throw new InvalidOperationException(
                           "Managed render component proxy type resolver is not installed.");
        return resolver(catalogEntry.HostAssembly, catalogEntry.HostType)
               ?? throw new TypeLoadException(
                   $"Generated proxy {catalogEntry.HostAssembly}!{catalogEntry.HostType} is unavailable.");
    }

    private static MethodInfo ResolveRenderMethod(
        Type componentType,
        PcCompatManagedRenderComponentCatalog.Entry catalogEntry)
    {
        var candidates = componentType
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(method => method.Name == catalogEntry.RenderMethod)
            .Where(method => method.ReturnType == typeof(void))
            .Where(method => method.GetParameters().Length == 1)
            .Where(method => method.GetParameters()[0].ParameterType.FullName ==
                             catalogEntry.RenderParameterType)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Managed render component {componentType.FullName} must declare exactly one " +
                $"void {catalogEntry.RenderMethod}({catalogEntry.RenderParameterType}); found " +
                candidates.Length + ".");
        }
        // DeclaredOnly deliberately: an inherited OnPopulateMesh would be the proxy's own, and
        // invoking that would recurse straight back into the hook.
        return candidates[0];
    }

    public static T? GetComponent<T>(object owner)
        => (T?)GetComponent(owner, typeof(T));

    public static T[] GetComponents<T>(object owner)
        => GetComponentsCore(owner, typeof(T)).Cast<T>().ToArray();

    public static object GetComponents(object owner, Type requestedType)
    {
        ArgumentNullException.ThrowIfNull(requestedType);
        var components = GetComponentsCore(owner, requestedType);
        var result = Array.CreateInstance(requestedType, components.Count);
        for (var index = 0; index < components.Count; index++)
            result.SetValue(components[index], index);
        return result;
    }

    public static bool TryGetComponent<T>(object owner, out T? component)
    {
        component = GetComponent<T>(owner);
        return component is not null;
    }

    public static bool TryGetComponent<T>(
        object owner,
        Type requestedType,
        out T? component)
    {
        var resolved = GetComponent(owner, requestedType);
        if (resolved is null)
        {
            component = default;
            return false;
        }
        if (resolved is not T typed)
        {
            throw new InvalidCastException(
                $"Resolved component {resolved.GetType().AssemblyQualifiedName} cannot be returned as " +
                typeof(T).AssemblyQualifiedName + ".");
        }
        component = typed;
        return true;
    }

    public static object? GetComponent(object owner, Type requestedType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(requestedType);
        RequireUnityMain();
        RequireExecutionContext();
        var operations = RequireHostOperations();
        if (!operations.IsNativeComponentType(requestedType))
            return GetManagedComponent(owner, requestedType);

        var source = TryGetEntry(owner, out var managedOwner)
            ? managedOwner.OwnerGameObject
            : owner;
        return NormalizeNativeComponentResult(
            operations,
            operations.GetNativeComponent(source, requestedType),
            requestedType,
            "GetComponent");
    }

    private static object? GetManagedComponent(object owner, Type requestedType)
        => GetManagedComponents(owner, requestedType).FirstOrDefault();

    private static IReadOnlyList<object> GetComponentsCore(object owner, Type requestedType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(requestedType);
        RequireUnityMain();
        RequireExecutionContext();
        var operations = RequireHostOperations();
        if (!operations.IsNativeComponentType(requestedType))
            return GetManagedComponents(owner, requestedType);

        var source = TryGetEntry(owner, out var managedOwner)
            ? managedOwner.OwnerGameObject
            : owner;
        var rawComponents = operations.GetNativeComponents(source, requestedType)
                            ?? throw new InvalidOperationException(
                                $"Native GetComponents returned null for {requestedType.AssemblyQualifiedName}.");
        if (rawComponents.Count == 0)
            return rawComponents;

        var components = new object[rawComponents.Count];
        for (var index = 0; index < rawComponents.Count; index++)
        {
            components[index] = NormalizeNativeComponentResult(
                operations,
                rawComponents[index],
                requestedType,
                "GetComponents")!;
        }
        return components;
    }

    private static object? NormalizeNativeComponentResult(
        PcCompatManagedComponentHostOperations operations,
        object? result,
        Type requestedType,
        string operation)
    {
        if (result is null || requestedType.IsInstanceOfType(result))
            return result;

        var pointer = operations.ReadNativeInstancePointer(result);
        if (pointer == 0)
        {
            throw new InvalidCastException(
                $"Native {operation} returned {result.GetType().AssemblyQualifiedName} for " +
                $"{requestedType.AssemblyQualifiedName}, and the result has no live native pointer.");
        }

        var wrapped = operations.WrapNativeProxyPointer(requestedType, (nint)pointer);
        if (!requestedType.IsInstanceOfType(wrapped))
        {
            throw new InvalidCastException(
                $"Native {operation} result for {requestedType.AssemblyQualifiedName} was rewrapped as " +
                wrapped.GetType().AssemblyQualifiedName + ".");
        }
        return wrapped;
    }

    private static IReadOnlyList<object> GetManagedComponents(object owner, Type requestedType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        ValidateManagedComponentQueryType(requestedType);
        if (!RequireHostOperations().IsManagedComponentTypeOwnedByMod(
                requestedType,
                execution.ModId))
        {
            throw new InvalidOperationException(
                $"Managed component query type is not owned by MOD '{execution.ModId}': " +
                requestedType.AssemblyQualifiedName);
        }
        var ownerSnapshot = ResolveOwner(owner);
        if (!ownerSnapshot.IsAlive || ownerSnapshot.Identity == 0)
            return Array.Empty<object>();

        lock (Gate)
        {
            var key = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
            if (!Sessions.TryGetValue(key, out var bucket))
                return Array.Empty<object>();
            return bucket.Entries
                .Where(entry =>
                    !entry.Destroying &&
                    entry.OwnerIdentity == ownerSnapshot.Identity &&
                    requestedType.IsAssignableFrom(entry.ComponentType))
                .Select(entry => entry.Instance)
                .ToArray();
        }
    }

    public static object GetGameObject(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        RequireExecutionContext();
        return ResolveLiveGameObjectForSource(source);
    }

    /// <summary>
    /// Owner-aware replacement for <c>GameObject.SetActive(bool)</c>. Unity activation has
    /// lifecycle effects, so it executes on the canonical native wrapper selected by the owner
    /// host after the current managed session has been validated.
    /// </summary>
    public static void SetActive(object source, bool active)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        RequireExecutionContext();
        var operations = RequireHostOperations();
        var gameObject = ResolveLiveGameObjectForSource(source);
        operations.SetNativeGameObjectActive(gameObject, active);
    }

    public static object GetTransform(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        RequireExecutionContext();
        var expectedOwnerIdentity = TryGetEntry(source, out var entry)
            ? entry.OwnerIdentity
            : (long?)null;
        var owner = ResolveOwner(source);
        if (!owner.IsAlive || owner.Identity == 0)
            throw new InvalidOperationException("Cannot resolve transform for a destroyed owner.");
        return RequireHostOperations().ResolveTransform(
                   ResolveLiveGameObject(owner, expectedOwnerIdentity))
               ?? throw new InvalidOperationException("Managed component host returned a null transform.");
    }

    public static bool GetEnabled(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        var operations = RequireHostOperations();
        if (!TryGetEntry(source, out var entry))
        {
            if (TryGetNativeLease(source, out var nativeLease))
                RequireLeaseOwner(nativeLease, execution);
            return ReadSharedNativeProperty(
                BehaviourEnabledDescriptor,
                source,
                execution,
                operations) is true;
        }

        RequireEntryOwner(entry, execution);
        return !entry.Destroying && entry.ReadEnabled();
    }

    /// <summary>
    /// Reads <c>RectTransform.anchoredPosition</c> as a boxed generated-proxy <c>Vector2</c>.
    /// </summary>
    /// <remarks>
    /// The read is routed through the arbitration registry for the same reason the write is: a MOD
    /// that repositions a game-owned rect samples the "original" position from this getter and
    /// restores it later. Two real MODs do exactly that on the same object - JipperOverlayer's
    /// <c>BetaWatermarkOriginalPos</c> and CheryTools' <c>ElementState.AnchoredPosition</c> - so
    /// whichever samples second would otherwise record the first one's offset as the game's
    /// original and permanently displace the watermark when it restores. Returning the caller's own
    /// contribution, or the untouched baseline if it has none, keeps each MOD's notion of
    /// "original" independent of the other's writes.
    /// </remarks>
    public static object GetAnchoredPosition(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        var operations = RequireHostOperations();
        if (TryGetNativeLease(source, out var nativeLease))
        {
            // A rect the MOD created itself is private layout state, not shared game state.
            RequireLeaseOwner(nativeLease, execution);
            return operations.ReadNativeAnchoredPosition(source);
        }
        return ReadSharedNativeProperty(AnchoredPositionDescriptor, source, execution, operations)
               ?? throw new InvalidOperationException(
                   "Managed component host returned a null anchoredPosition.");
    }

    /// <summary>
    /// Writes <c>RectTransform.anchoredPosition</c> from a boxed generated-proxy <c>Vector2</c>,
    /// arbitrated across MODs when the rect belongs to the game.
    /// </summary>
    public static void SetAnchoredPosition(object source, object value)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(value);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        var operations = RequireHostOperations();
        if (TryGetNativeLease(source, out var nativeLease))
        {
            RequireLeaseOwner(nativeLease, execution);
            operations.WriteNativeAnchoredPosition(source, value);
            return;
        }
        SetSharedNativeProperty(AnchoredPositionDescriptor, source, value, execution, operations);
    }

    public static void SetEnabled(object source, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        var operations = RequireHostOperations();
        if (!TryGetEntry(source, out var entry))
        {
            if (TryGetNativeLease(source, out var nativeLease))
            {
                RequireLeaseOwner(nativeLease, execution);
                operations.WriteNativeBehaviourEnabled(source, enabled);
                return;
            }
            SetSharedNativeProperty(
                BehaviourEnabledDescriptor,
                source,
                enabled,
                execution,
                operations);
            return;
        }

        RequireEntryOwner(entry, execution);
        if (entry.Destroying)
            throw new InvalidOperationException("Cannot change enabled state on a destroying managed component.");

        var wasEnabled = entry.ReadEnabled();
        entry.SetBridgeEnabled(enabled);
        var isEnabled = entry.ReadEnabled();
        if (wasEnabled == isEnabled)
            return;

        var owner = ResolveOwner(entry.OwnerGameObject);
        if (!owner.IsAlive || owner.Identity == 0)
        {
            var cleanupError = DestroyEntry(entry);
            if (cleanupError != null)
                throw new InvalidOperationException(cleanupError);
            return;
        }
        if (owner.Identity != entry.OwnerIdentity)
        {
            throw new InvalidOperationException(
                $"Owner identity changed from 0x{entry.OwnerIdentity:x} to 0x{owner.Identity:x}.");
        }

        if (!isEnabled)
        {
            if (!entry.Active)
                return;
            entry.Active = false;
            Invoke(entry, entry.OnDisable, "OnDisable");
            return;
        }
        if (!owner.IsActive || entry.Active)
            return;
        entry.Active = true;
        Invoke(entry, entry.OnEnable, "OnEnable");
    }

    public static void DontDestroyOnLoad(object? target)
    {
        RequireUnityMain();
        var execution = RequireExecutionContext();
        if (target is null)
            return;

        var operations = RequireHostOperations();
        operations.DontDestroyNativeObject(target);
        if (!operations.IsGameObject(target))
            return;
        var owner = ResolveOwner(target);
        var key = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        var owned = false;
        var persistentCount = 0;
        lock (Gate)
        {
            owned = Components.Values.Any(entry =>
                entry.OwnerIdentity == owner.Identity &&
                entry.Session.Generation == key.Generation &&
                StringComparer.OrdinalIgnoreCase.Equals(entry.Session.ModId, key.ModId));
            owned |= NativeLeases.Values.Any(lease =>
                lease.OwnerIdentity == owner.Identity &&
                lease.Session.Generation == key.Generation &&
                StringComparer.OrdinalIgnoreCase.Equals(lease.Session.ModId, key.ModId));
            if (owned)
            {
                if (!Sessions.TryGetValue(key, out var bucket))
                {
                    bucket = new SessionBucket();
                    Sessions.Add(key, bucket);
                }
                bucket.PersistentObjects.Add(target);
                persistentCount = bucket.PersistentObjects.Count;
            }
        }
        Logger.Info(
            nameof(PcCompatManagedComponentBridge),
            $"{UnloadDebugTag} persistent-{(owned ? "registered" : "forward-only")} " +
            $"mod={key.ModId} generation={key.Generation} owner={owner.Identity} " +
            $"persistent={persistentCount} type={target.GetType().FullName} " +
            $"tid={Environment.CurrentManagedThreadId}");
    }

    public static void Destroy(object? target)
    {
        RequireUnityMain();
        var execution = RequireExecutionContext();
        if (target is null)
            return;

        var operations = RequireHostOperations();
        var key = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        var persistent = IsPersistentObjectRegistered(key, target);
        var failures = new List<string>();
        if (TryGetEntry(target, out var component))
        {
            AddFailure(failures, DestroyEntry(component));
            ThrowDestroyFailures(target, failures);
            return;
        }

        if (TryGetNativeLease(target, out var nativeLease))
        {
            RequireLeaseOwner(nativeLease, execution);
            AddFailure(failures, DestroyNativeLease(nativeLease, operations));
            ThrowDestroyFailures(target, failures);
            return;
        }

        if (operations.IsGameObject(target))
        {
            var owner = ResolveOwner(target);
            ComponentEntry[] entries;
            NativeObjectLease[] nativeLeases;
            lock (Gate)
            {
                entries = Components.Values
                    .Where(entry => !entry.Destroying && entry.OwnerIdentity == owner.Identity)
                    .ToArray();
                nativeLeases = NativeLeases.Values
                    .Where(lease => !lease.Destroying && lease.OwnerIdentity == owner.Identity)
                    .ToArray();
            }
            foreach (var entry in entries)
                RequireEntryOwner(entry, execution);
            foreach (var lease in nativeLeases)
                RequireLeaseOwner(lease, execution);
            foreach (var entry in entries)
                AddFailure(failures, DestroyEntry(entry));
            foreach (var lease in nativeLeases)
                AddFailure(failures, DestroyNativeLease(lease, operations));
        }

        var nativeDestroyed = DestroyNativeObject(operations, target, failures);
        var retired = nativeDestroyed && RetirePersistentObject(key, target);
        if (persistent)
        {
            Logger.Info(
                nameof(PcCompatManagedComponentBridge),
                $"{UnloadDebugTag} persistent-explicit-destroy mod={key.ModId} " +
                $"generation={key.Generation} native={nativeDestroyed} retired={retired} " +
                $"failures={failures.Count} tid={Environment.CurrentManagedThreadId}");
        }
        ThrowDestroyFailures(target, failures);
    }

    public static void Destroy(object? target, float delay)
    {
        RequireUnityMain();
        var execution = RequireExecutionContext();
        if (target is null)
            return;
        if (!float.IsFinite(delay) || delay < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delay),
                delay,
                "Managed destroy delay must be finite and non-negative.");
        }
        if (delay == 0f)
        {
            Destroy(target);
            return;
        }

        var operations = RequireHostOperations();
        if (TryGetEntry(target, out var component))
        {
            ScheduleDestroy(component, delay);
            return;
        }

        if (TryGetNativeLease(target, out var nativeLease))
        {
            RequireLeaseOwner(nativeLease, execution);
            operations.DestroyNativeObjectDelayed(target, delay);
            MarkNativeLeaseDestroyScheduled(nativeLease);
            return;
        }

        operations.DestroyNativeObjectDelayed(target, delay);
        if (!operations.IsGameObject(target))
            return;

        var owner = ResolveOwner(target);
        ComponentEntry[] entries;
        NativeObjectLease[] nativeLeases;
        lock (Gate)
        {
            entries = Components.Values
                .Where(entry => !entry.Destroying && entry.OwnerIdentity == owner.Identity)
                .ToArray();
            nativeLeases = NativeLeases.Values
                .Where(lease => !lease.Destroying && lease.OwnerIdentity == owner.Identity)
                .ToArray();
        }
        foreach (var entry in entries)
            RequireEntryOwner(entry, execution);
        foreach (var lease in nativeLeases)
            RequireLeaseOwner(lease, execution);
        foreach (var entry in entries)
            ScheduleDestroy(entry, delay);
        foreach (var lease in nativeLeases)
            MarkNativeLeaseDestroyScheduled(lease);
    }

    public static object StartCoroutine(object source, IEnumerator routine)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(routine);
        RequireUnityMain();
        var entry = RequireCoroutineOwner(source);
        var operations = RequireHostOperations();
        long frameGeneration;
        double scaledTime;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(entry.Session, out var bucket))
                throw new InvalidOperationException("Managed coroutine owner session is unavailable.");
            frameGeneration = bucket.FrameGeneration;
            scaledTime = bucket.ScaledTime;
        }
        return entry.StartExplicitCoroutine(
            routine,
            frameGeneration,
            scaledTime,
            Stopwatch.GetTimestamp(),
            operations.ResolveYieldDelay);
    }

    public static object StartCoroutine(object source, string methodName)
        => StartNamedCoroutine(source, methodName, null, hasArgument: false);

    public static object StartCoroutine(object source, string methodName, object? value)
        => StartNamedCoroutine(source, methodName, value, hasArgument: true);

    public static void StopCoroutine(object source, IEnumerator routine)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(routine);
        RequireUnityMain();
        RequireCoroutineOwner(source).StopExplicitCoroutine(routine);
    }

    public static void StopCoroutine(object source, object handle)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handle);
        RequireUnityMain();
        RequireCoroutineOwner(source).StopExplicitCoroutine(handle);
    }

    public static void StopCoroutine(object source, string methodName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        RequireUnityMain();
        RequireCoroutineOwner(source).StopExplicitCoroutines(methodName);
    }

    public static void StopAllCoroutines(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        RequireCoroutineOwner(source).CancelCoroutines();
    }

    private static object StartNamedCoroutine(
        object source,
        string methodName,
        object? value,
        bool hasArgument)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        RequireUnityMain();
        var entry = RequireCoroutineOwner(source);
        var operations = RequireHostOperations();
        long frameGeneration;
        double scaledTime;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(entry.Session, out var bucket))
                throw new InvalidOperationException("Managed coroutine owner session is unavailable.");
            frameGeneration = bucket.FrameGeneration;
            scaledTime = bucket.ScaledTime;
        }
        return entry.StartNamedCoroutine(
            methodName,
            value,
            hasArgument,
            frameGeneration,
            scaledTime,
            Stopwatch.GetTimestamp(),
            operations.ResolveYieldDelay);
    }

    public static bool HasComponents(string modId, long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var states = Volatile.Read(ref s_dispatchStates);
        return states.TryGetValue(
                   new SessionKey(modId, sessionGeneration),
                   out var state) &&
               state.HasComponents;
    }

    public static IReadOnlyList<PcCompatUnityObjectLeaseSnapshot> SnapshotObjectLeases(
        string modId,
        long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            if (!Sessions.TryGetValue(
                    new SessionKey(modId, sessionGeneration),
                    out var bucket))
            {
                return Array.Empty<PcCompatUnityObjectLeaseSnapshot>();
            }
            return bucket.NativeLeases
                .Select(lease => new PcCompatUnityObjectLeaseSnapshot(
                    lease.OwnerIdentity,
                    "NativeComponent",
                    lease.ObjectType.FullName ?? lease.ObjectType.Name,
                    lease.Destroying,
                    lease.DestroyScheduled))
                .ToArray();
        }
    }

    public static bool HasOnGUIComponents(string modId, long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var states = Volatile.Read(ref s_dispatchStates);
        return states.TryGetValue(
                   new SessionKey(modId, sessionGeneration),
                   out var state) &&
               state.HasOnGUIComponents;
    }

    public static IReadOnlyList<object> SnapshotOwnerGameObjects(
        string modId,
        long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            if (!Sessions.TryGetValue(
                    new SessionKey(modId, sessionGeneration),
                    out var bucket))
                return Array.Empty<object>();

            var owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var entry in bucket.Entries)
            {
                if (!entry.Destroying)
                    owners.Add(entry.OwnerGameObject);
            }
            foreach (var lease in bucket.NativeLeases)
            {
                if (!lease.Destroying)
                    owners.Add(lease.OwnerGameObject);
            }
            return owners.ToArray();
        }
    }

    public static PcCompatManagedComponentLifecycleSnapshot SnapshotLifecycle(
        string modId,
        long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            if (!Sessions.TryGetValue(
                    new SessionKey(modId, sessionGeneration),
                    out var bucket))
                return new PcCompatManagedComponentLifecycleSnapshot();
            return new PcCompatManagedComponentLifecycleSnapshot
            {
                FrameGeneration = bucket.FrameGeneration,
                Components = bucket.Entries
                    .Where(entry => !entry.Destroying)
                    .Select(entry => entry.SnapshotLifecycle())
                    .ToArray()
            };
        }
    }

    public static bool TryDispatchFrame(
        string modId,
        long sessionGeneration,
        float deltaTime,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        error = null;
        if (!float.IsFinite(deltaTime) || deltaTime < 0f)
        {
            error = $"Invalid managed component deltaTime: {deltaTime}.";
            return false;
        }

        ComponentEntry[] entries;
        List<ComponentEntry> lateUpdates;
        SessionBucket bucket;
        lock (Gate)
        {
            var key = new SessionKey(modId, sessionGeneration);
            if (!Sessions.TryGetValue(key, out bucket!) || bucket.Entries.Count == 0)
                return true;
            if (bucket.Dispatching)
            {
                error = "Managed component frame dispatch re-entry was rejected.";
                return false;
            }
            bucket.Dispatching = true;
            entries = bucket.GetDispatchSnapshot();
            lateUpdates = bucket.LateUpdates;
            lateUpdates.Clear();
        }

        try
        {
            RequireUnityMain();
            var operations = RequireHostOperations();
            var scaledDeltaTime = entries.Any(entry => entry.RequiresScaledClock)
                ? operations.ReadScaledDeltaTime()
                : 0f;
            if (!float.IsFinite(scaledDeltaTime) || scaledDeltaTime < 0f)
            {
                throw new InvalidOperationException(
                    $"Invalid Unity scaled deltaTime: {scaledDeltaTime}.");
            }
            bucket.ScaledTime += scaledDeltaTime;
            bucket.FrameGeneration++;
            var nowTimestamp = Stopwatch.GetTimestamp();
            PcCompatDeepDebug.WritePeriodic(
                "component-inventory",
                modId + "\0" + sessionGeneration,
                TimeSpan.FromSeconds(30),
                () => DescribeComponentInventory(
                    modId,
                    sessionGeneration,
                    bucket.FrameGeneration,
                    entries));
            foreach (var entry in entries)
            {
                if (!IsRegistered(entry))
                    continue;
                try
                {
                    if (entry.IsDestroyDue(bucket.ScaledTime))
                    {
                        var cleanupError = DestroyEntry(entry);
                        if (cleanupError != null)
                            throw new InvalidOperationException(cleanupError);
                        continue;
                    }
                    var owner = ResolveOwner(entry.OwnerGameObject);
                    if (!owner.IsAlive)
                    {
                        var cleanupError = DestroyEntry(entry);
                        if (cleanupError != null)
                            throw new InvalidOperationException(cleanupError);
                        continue;
                    }
                    if (owner.Identity != entry.OwnerIdentity)
                    {
                        throw new InvalidOperationException(
                            $"Owner identity changed from 0x{entry.OwnerIdentity:x} to 0x{owner.Identity:x}.");
                    }

                    if (!owner.IsActive)
                    {
                        if (entry.Active)
                        {
                            entry.Active = false;
                            Invoke(entry, entry.OnDisable, "OnDisable");
                        }
                        entry.CancelCoroutines();
                        continue;
                    }

                    var enabled = entry.ReadEnabled();
                    if (!enabled)
                    {
                        if (entry.Active)
                        {
                            entry.Active = false;
                            Invoke(entry, entry.OnDisable, "OnDisable");
                        }
                        if (!IsRegistered(entry))
                            continue;
                        entry.AdvanceCoroutines(
                            bucket.FrameGeneration,
                            bucket.ScaledTime,
                            nowTimestamp,
                            operations.ResolveYieldDelay);
                        continue;
                    }

                    if (!entry.Active)
                    {
                        entry.Active = true;
                        Invoke(entry, entry.OnEnable, "OnEnable");
                        if (!IsRegistered(entry))
                            continue;
                    }
                    if (!entry.Started)
                    {
                        entry.BeginStart(
                            bucket.FrameGeneration,
                            bucket.ScaledTime,
                            nowTimestamp,
                            operations.ResolveYieldDelay);
                        entry.Started = true;
                        if (!IsRegistered(entry))
                            continue;
                        entry.AdvanceCoroutines(
                            bucket.FrameGeneration,
                            bucket.ScaledTime,
                            nowTimestamp,
                            operations.ResolveYieldDelay);
                    }
                    else
                    {
                        entry.AdvanceCoroutines(
                            bucket.FrameGeneration,
                            bucket.ScaledTime,
                            nowTimestamp,
                            operations.ResolveYieldDelay);
                    }
                    Invoke(entry, entry.Update, "Update");
                    if (IsRegistered(entry) && entry.Active)
                        lateUpdates.Add(entry);
                }
                catch (Exception exception)
                {
                    var cleanupError = DestroyEntry(entry);
                    error = FormatFailure(entry, "Update", exception, cleanupError);
                    return false;
                }
            }

            foreach (var entry in lateUpdates)
            {
                if (!IsRegistered(entry) || !entry.Active)
                    continue;
                try
                {
                    Invoke(entry, entry.LateUpdate, "LateUpdate");
                }
                catch (Exception exception)
                {
                    var cleanupError = DestroyEntry(entry);
                    error = FormatFailure(entry, "LateUpdate", exception, cleanupError);
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            error = Unwrap(exception).ToString();
            return false;
        }
        finally
        {
            lock (Gate)
            {
                bucket.LateUpdates.Clear();
                bucket.Dispatching = false;
            }
        }
    }

    // OnGUI is dispatched from inside Unity's IMGUI event pump (native hook on
    // GUIUtility.ProcessEvent), not from the frame callback, so mod GUI /
    // GUILayout calls observe a valid IMGUI context and the real Event.current.
    // Only entries already activated by frame dispatch (OnEnable delivered) with
    // an alive, active, enabled owner participate.
    public static bool TryDispatchOnGUI(
        string modId,
        long sessionGeneration,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        error = null;
        ComponentEntry[] entries;
        SessionBucket bucket;
        lock (Gate)
        {
            var key = new SessionKey(modId, sessionGeneration);
            if (!Sessions.TryGetValue(key, out bucket!) || bucket.Entries.Count == 0)
                return true;
            if (bucket.DispatchingOnGUI)
            {
                error = "Managed component OnGUI dispatch re-entry was rejected.";
                return false;
            }
            bucket.DispatchingOnGUI = true;
            entries = bucket.GetDispatchSnapshot();
        }

        try
        {
            RequireUnityMain();
            foreach (var entry in entries)
            {
                if (!IsRegistered(entry) || entry.OnGUI == null || !entry.Active)
                    continue;
                try
                {
                    var owner = ResolveOwner(entry.OwnerGameObject);
                    if (!owner.IsAlive || !owner.IsActive)
                        continue;
                    if (owner.Identity != entry.OwnerIdentity)
                    {
                        throw new InvalidOperationException(
                            $"Owner identity changed from 0x{entry.OwnerIdentity:x} to 0x{owner.Identity:x}.");
                    }
                    if (!entry.ReadEnabled())
                        continue;
                    Invoke(entry, entry.OnGUI, "OnGUI");
                }
                catch (Exception exception)
                {
                    var cleanupError = DestroyEntry(entry);
                    error = FormatFailure(entry, "OnGUI", exception, cleanupError);
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            error = Unwrap(exception).ToString();
            return false;
        }
        finally
        {
            lock (Gate)
            {
                bucket.DispatchingOnGUI = false;
            }
        }
    }

    public static bool TryClearSession(
        string modId,
        long sessionGeneration,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        error = null;
        // Before anything else, and unconditionally: stop native from dispatching render callbacks
        // into a session that is going away. This runs even for an empty session because the native
        // set is the one piece of state that outlives the managed tables - leaving a pointer in it
        // after teardown would let the hook look up a binding that no longer exists.
        ClearRenderComponentsForSession(new SessionKey(modId, sessionGeneration));
        ComponentEntry[] entries;
        NativeObjectLease[] nativeLeases;
        SharedPropertyContribution[] sharedPropertyContributions;
        object[] persistentObjects;
        var key = new SessionKey(modId, sessionGeneration);
        var empty = false;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(key, out var bucket) ||
                bucket.IsEmpty)
            {
                empty = true;
                entries = [];
                nativeLeases = [];
                sharedPropertyContributions = [];
                persistentObjects = [];
            }
            else
            {
                entries = bucket.Entries.ToArray();
                nativeLeases = bucket.NativeLeases.ToArray();
                sharedPropertyContributions = bucket.SharedPropertyContributions.ToArray();
                persistentObjects = bucket.PersistentObjects.ToArray();
            }
        }
        if (empty)
        {
            Logger.Info(
                nameof(PcCompatManagedComponentBridge),
                $"{UnloadDebugTag} session-clear-empty mod={key.ModId} " +
                $"generation={key.Generation} unityMain={PcCompatUnityMainExecutionContext.IsActive} " +
                $"tid={Environment.CurrentManagedThreadId}");
            return true;
        }

        Logger.Info(
            nameof(PcCompatManagedComponentBridge),
            $"{UnloadDebugTag} session-clear-enter mod={key.ModId} generation={key.Generation} " +
            $"entries={entries.Length} nativeLeases={nativeLeases.Length} " +
            $"sharedProperties={sharedPropertyContributions.Length} " +
            $"persistent={persistentObjects.Length} " +
            $"unityMain={PcCompatUnityMainExecutionContext.IsActive} " +
            $"tid={Environment.CurrentManagedThreadId}");

        if (!PcCompatUnityMainExecutionContext.IsActive)
        {
            bool demandChanged;
            lock (Gate)
            {
                foreach (var entry in entries)
                {
                    MarkManagedComponentRetired(entry);
                    entry.Destroying = true;
                    Components.Remove(entry.Instance);
                }
                foreach (var lease in nativeLeases)
                {
                    NativeLeases.Remove(lease.Target);
                    if (lease.NativeIdentity != 0 &&
                        NativeLeasesByIdentity.TryGetValue(lease.NativeIdentity, out var byIdentity) &&
                        ReferenceEquals(byIdentity, lease))
                    {
                        NativeLeasesByIdentity.Remove(lease.NativeIdentity);
                    }
                }
                DetachSharedNativePropertiesUnsafe(sharedPropertyContributions);
                Sessions.Remove(key);
                demandChanged = PublishDispatchStateLocked(key, null);
            }
            if (demandChanged)
                NotifyDemandChanged();
            foreach (var entry in entries)
                RetireOwnedUnityObject(entry.Session, ManagedComponentIdentity(entry));
            foreach (var lease in nativeLeases)
                RetireOwnedUnityObject(lease.Session, NativeLeaseIdentity(lease));
            error =
                "Managed component cleanup occurred outside UnityMain; entries, native leases, and " +
                "persistent objects were detached without callbacks.";
            Logger.Error(
                nameof(PcCompatManagedComponentBridge),
                $"{UnloadDebugTag} session-clear-detached mod={key.ModId} generation={key.Generation} " +
                $"entries={entries.Length} nativeLeases={nativeLeases.Length} " +
                $"sharedProperties={sharedPropertyContributions.Length} " +
                $"persistent={persistentObjects.Length} " +
                $"tid={Environment.CurrentManagedThreadId}");
            return false;
        }

        var failures = new List<string>();
        foreach (var entry in entries)
        {
            var failure = DestroyEntry(entry);
            if (failure != null)
                failures.Add(failure);
        }
        var operations = RequireHostOperations();
        foreach (var nativeLease in nativeLeases)
            AddFailure(failures, DestroyNativeLease(nativeLease, operations));
        foreach (var contribution in sharedPropertyContributions)
            AddFailure(failures, ClearSharedNativeProperty(contribution, operations));
        foreach (var persistentObject in persistentObjects)
        {
            if (!IsPersistentObjectRegistered(key, persistentObject))
                continue;
            var nativeDestroyed = DestroyNativeObject(operations, persistentObject, failures);
            var retired = nativeDestroyed && RetirePersistentObject(key, persistentObject);
            Logger.Info(
                nameof(PcCompatManagedComponentBridge),
                $"{UnloadDebugTag} session-persistent-destroy mod={key.ModId} " +
                $"generation={key.Generation} native={nativeDestroyed} retired={retired} " +
                $"type={persistentObject.GetType().FullName} failures={failures.Count} " +
                $"tid={Environment.CurrentManagedThreadId}");
        }
        lock (Gate)
        {
            if (Sessions.TryGetValue(key, out var bucket) &&
                bucket.IsEmpty)
            {
                Sessions.Remove(key);
            }
        }
        if (failures.Count == 0)
        {
            Logger.Info(
                nameof(PcCompatManagedComponentBridge),
                $"{UnloadDebugTag} session-clear-complete mod={key.ModId} " +
                $"generation={key.Generation} failures=0 tid={Environment.CurrentManagedThreadId}");
            return true;
        }
        error = string.Join(Environment.NewLine, failures);
        Logger.Error(
            nameof(PcCompatManagedComponentBridge),
            $"{UnloadDebugTag} session-clear-failed mod={key.ModId} generation={key.Generation} " +
            $"failures={failures.Count} error={error}");
        return false;
    }

    private static void Register(ComponentEntry entry)
    {
        var identity = ManagedComponentIdentity(entry);
        if (!TryRegisterOwnedUnityObject(entry.Session, identity))
        {
            throw new InvalidOperationException(
                $"Managed Unity object ownership registration failed mod={entry.Session.ModId} " +
                $"generation={entry.Session.Generation} type={entry.ComponentType.FullName}.");
        }
        try
        {
            bool demandChanged;
            lock (Gate)
            {
                if (!Sessions.TryGetValue(entry.Session, out var bucket))
                {
                    bucket = new SessionBucket();
                    Sessions.Add(entry.Session, bucket);
                }
                bucket.Entries.Add(entry);
                bucket.InvalidateSnapshot();
                Components.Add(entry.Instance, entry);
                entry.MarkRegistered();
                ManagedComponentObjectStates
                    .GetValue(entry.Instance, static _ => new ManagedComponentObjectState())
                    .MarkAlive();
                demandChanged = PublishDispatchStateLocked(entry.Session, bucket);
            }
            if (demandChanged)
                NotifyDemandChanged();
            PcCompatDeepDebug.WriteSampled(
                "component-register",
                entry.Session.ModId + "\0" + entry.Session.Generation + "\0" +
                (entry.ComponentType.FullName ?? entry.ComponentType.Name),
                count =>
                    $"count={count} mod={entry.Session.ModId} generation={entry.Session.Generation} " +
                    $"component={PcCompatDeepDebug.DescribeObject(entry.Instance)} " +
                    $"owner=0x{entry.OwnerIdentity:X} ownerObject={PcCompatDeepDebug.DescribeObject(entry.OwnerGameObject)} " +
                    $"lifecycle={entry.DescribeLifecycleBindings()} fields=[{PcCompatDeepDebug.DescribeFields(entry.Instance)}]",
                first: 2,
                periodic: 128);
        }
        catch
        {
            RetireOwnedUnityObject(entry.Session, identity);
            throw;
        }
    }

    private static object? ReadSharedNativeProperty(
        SharedPropertyDescriptor descriptor,
        object target,
        PcCompatManagedExecutionState execution,
        PcCompatManagedComponentHostOperations operations)
    {
        var session = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        lock (Gate)
        {
            if (SharedProperties.TryGetValue(
                    new SharedPropertyKey(target, descriptor.Name),
                    out var state))
            {
                // A MOD sees the game's value plus its own writes, never another MOD's. Reading the
                // live native value here would hand back whichever MOD currently projects, so the
                // reader would treat a peer's offset as the game's own - and record it as the
                // "original" it restores on unload. Falling back to the baseline keeps every MOD's
                // notion of the original pinned to what the game actually had.
                //
                // The registry only holds an entry while at least one contribution is live, so this
                // branch never shadows an uncontested property: with no contributors at all the read
                // goes straight to native below. A game-side change to a property some MOD is
                // currently holding is therefore invisible until that MOD releases - the necessary
                // cost of not letting MODs observe each other's writes.
                return state.Contributions.TryGetValue(session, out var contribution)
                    ? contribution.Value
                    : state.Baseline;
            }
        }
        return descriptor.Read(operations, target);
    }

    private static void SetSharedNativeProperty(
        SharedPropertyDescriptor descriptor,
        object target,
        object? value,
        PcCompatManagedExecutionState execution,
        PcCompatManagedComponentHostOperations operations)
    {
        var session = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        var propertyKey = new SharedPropertyKey(target, descriptor.Name);
        SharedPropertyState state;
        SharedPropertyContribution contribution;
        var existed = false;
        object? previousValue = null;
        object? projectedValue;

        lock (Gate)
        {
            if (!SharedProperties.TryGetValue(propertyKey, out state!))
            {
                state = new SharedPropertyState(
                    propertyKey,
                    descriptor,
                    descriptor.Read(operations, target));
                SharedProperties.Add(propertyKey, state);
            }

            if (state.Contributions.Values.Any(item =>
                    StringComparer.OrdinalIgnoreCase.Equals(item.Session.ModId, session.ModId) &&
                    item.Session.Generation > session.Generation))
            {
                throw new InvalidOperationException(
                    $"An older MOD session cannot mutate {descriptor.Name} after a newer session " +
                    "is active.");
            }

            foreach (var superseded in state.Contributions.Values
                         .Where(item =>
                             StringComparer.OrdinalIgnoreCase.Equals(item.Session.ModId, session.ModId) &&
                             item.Session.Generation < session.Generation)
                         .ToArray())
            {
                state.Contributions.Remove(superseded.Session);
                RemoveSharedPropertyContributionFromBucketUnsafe(superseded);
            }

            if (state.Contributions.TryGetValue(session, out contribution!))
            {
                existed = true;
                previousValue = contribution.Value;
                contribution.Value = value;
            }
            else
            {
                contribution = new SharedPropertyContribution(
                    session,
                    propertyKey,
                    descriptor,
                    value,
                    Interlocked.Increment(ref s_nextSharedPropertyContributionSequence));
                state.Contributions.Add(session, contribution);
                GetOrCreateSessionBucketUnsafe(session)
                    .SharedPropertyContributions.Add(contribution);
            }
            projectedValue = SelectProjectedValue(state);
        }

        try
        {
            descriptor.Write(operations, target, projectedValue);
        }
        catch (Exception exception)
        {
            var restoreProjectedValue = false;
            object? rollbackValue = null;
            lock (Gate)
            {
                if (SharedProperties.TryGetValue(propertyKey, out var current) &&
                    ReferenceEquals(current, state) &&
                    current.Contributions.TryGetValue(session, out var currentContribution) &&
                    ReferenceEquals(currentContribution, contribution))
                {
                    if (existed)
                    {
                        currentContribution.Value = previousValue;
                        rollbackValue = SelectProjectedValue(current);
                    }
                    else
                    {
                        current.Contributions.Remove(session);
                        RemoveSharedPropertyContributionFromBucketUnsafe(contribution);
                        if (current.Contributions.Count == 0)
                        {
                            rollbackValue = current.Baseline;
                            SharedProperties.Remove(propertyKey);
                        }
                        else
                        {
                            rollbackValue = SelectProjectedValue(current);
                        }
                    }
                    restoreProjectedValue = true;
                }
            }
            if (restoreProjectedValue)
            {
                try
                {
                    descriptor.Write(operations, target, rollbackValue);
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        $"{descriptor.Name} contribution write failed and rollback also failed.",
                        new AggregateException(exception, rollbackException));
                }
            }
            throw;
        }
    }

    private static string? ClearSharedNativeProperty(
        SharedPropertyContribution contribution,
        PcCompatManagedComponentHostOperations operations)
    {
        object? projectedValue;
        SharedPropertyState state;
        lock (Gate)
        {
            if (!SharedProperties.TryGetValue(contribution.Key, out state!) ||
                !state.Contributions.TryGetValue(contribution.Session, out var current) ||
                !ReferenceEquals(current, contribution))
            {
                return null;
            }
            projectedValue = SelectProjectedValue(state, contribution);
        }

        try
        {
            if (operations.IsNativeObjectAlive(contribution.Key.Target))
                contribution.Descriptor.Write(operations, contribution.Key.Target, projectedValue);
        }
        catch (Exception exception)
        {
            return $"Shared {contribution.Descriptor.Name} restore failed for " +
                   contribution.Key.Target.GetType().AssemblyQualifiedName + ": " +
                   Unwrap(exception);
        }

        lock (Gate)
        {
            if (!SharedProperties.TryGetValue(contribution.Key, out state!) ||
                !state.Contributions.TryGetValue(contribution.Session, out var current) ||
                !ReferenceEquals(current, contribution))
            {
                return null;
            }
            state.Contributions.Remove(contribution.Session);
            RemoveSharedPropertyContributionFromBucketUnsafe(contribution);
            if (state.Contributions.Count == 0)
                SharedProperties.Remove(contribution.Key);
        }
        return null;
    }

    private static void DetachSharedNativePropertiesUnsafe(
        IReadOnlyList<SharedPropertyContribution> contributions)
    {
        foreach (var contribution in contributions)
        {
            if (!SharedProperties.TryGetValue(
                    contribution.Key,
                    out var state) ||
                !state.Contributions.TryGetValue(contribution.Session, out var current) ||
                !ReferenceEquals(current, contribution))
            {
                continue;
            }
            state.Contributions.Remove(contribution.Session);
            RemoveSharedPropertyContributionFromBucketUnsafe(contribution);
            if (state.Contributions.Count == 0)
                SharedProperties.Remove(contribution.Key);
        }
    }

    private static SessionBucket GetOrCreateSessionBucketUnsafe(SessionKey session)
    {
        if (Sessions.TryGetValue(session, out var bucket))
            return bucket;
        bucket = new SessionBucket();
        Sessions.Add(session, bucket);
        return bucket;
    }

    private static void RemoveSharedPropertyContributionFromBucketUnsafe(
        SharedPropertyContribution contribution)
    {
        if (!Sessions.TryGetValue(contribution.Session, out var bucket))
            return;
        bucket.SharedPropertyContributions.Remove(contribution);
        if (bucket.IsEmpty)
            Sessions.Remove(contribution.Session);
    }

    /// <summary>
    /// Last writer wins among live contributions, falling back to the game's own baseline once the
    /// last MOD releases. Sequence is a global monotonic counter, so the winner is the MOD that
    /// most recently established a contribution - not the one that most recently re-wrote the same
    /// value, which keeps a MOD that writes every frame from starving one that writes once.
    /// </summary>
    private static object? SelectProjectedValue(
        SharedPropertyState state,
        SharedPropertyContribution? excluded = null)
    {
        SharedPropertyContribution? winner = null;
        foreach (var candidate in state.Contributions.Values)
        {
            if (ReferenceEquals(candidate, excluded))
                continue;
            if (winner == null || candidate.Sequence > winner.Sequence)
                winner = candidate;
        }
        return winner != null ? winner.Value : state.Baseline;
    }

    private static void RegisterNativeLease(
        SessionKey session,
        PcCompatManagedComponentOwnerSnapshot owner,
        object target,
        Type objectType)
    {
        var operations = RequireHostOperations();
        var nativeIdentity = operations.ReadNativeInstancePointer(target);
        var lease = new NativeObjectLease(
            session,
            owner.Identity,
            owner.GameObject,
            target,
            objectType,
            nativeIdentity);
        var identity = NativeLeaseIdentity(lease);
        lock (Gate)
        {
            if (NativeLeases.ContainsKey(target) ||
                (nativeIdentity != 0 && NativeLeasesByIdentity.ContainsKey(nativeIdentity)))
                throw new InvalidOperationException("Native Unity object is already leased.");
        }
        if (!TryRegisterOwnedUnityObject(session, identity))
        {
            throw new InvalidOperationException(
                $"Native Unity object ownership registration failed mod={session.ModId} " +
                $"generation={session.Generation} type={objectType.FullName}.");
        }
        try
        {
            lock (Gate)
            {
                if (NativeLeases.ContainsKey(target) ||
                    (nativeIdentity != 0 && NativeLeasesByIdentity.ContainsKey(nativeIdentity)))
                    throw new InvalidOperationException("Native Unity object is already leased.");
                if (!Sessions.TryGetValue(session, out var bucket))
                {
                    bucket = new SessionBucket();
                    Sessions.Add(session, bucket);
                }
                bucket.NativeLeases.Add(lease);
                NativeLeases.Add(target, lease);
                if (nativeIdentity != 0)
                    NativeLeasesByIdentity.Add(nativeIdentity, lease);
            }
        }
        catch
        {
            RetireOwnedUnityObject(session, identity);
            throw;
        }
    }

    private static bool IsRegistered(ComponentEntry entry)
        => entry.IsRegistered;

    private static bool TryGetManagedComponentObjectState(
        object? value,
        out ManagedComponentObjectState? state)
    {
        if (value is null)
        {
            state = null;
            return false;
        }
        return ManagedComponentObjectStates.TryGetValue(value, out state);
    }

    private static void MarkManagedComponentRetired(ComponentEntry entry)
    {
        entry.MarkRetired();
        ManagedComponentObjectStates
            .GetValue(entry.Instance, static _ => new ManagedComponentObjectState())
            .MarkRetired();
    }

    /// <summary>
    /// Drops one session's render bindings and, first, their native registrations.
    /// </summary>
    /// <remarks>
    /// Ordering matters and is the reverse of registration: native stops dispatching before the
    /// managed table loses the binding. The other order would leave a window where the hook resolves
    /// a pointer whose binding is already gone - harmless in itself, since the lookup just misses and
    /// the host draws its own quad, but it would also mean the host quad appears for one frame instead
    /// of the object simply being destroyed.
    /// </remarks>
    private static void ClearRenderComponentsForSession(SessionKey session)
    {
        RenderComponentBinding[] bindings;
        lock (Gate)
        {
            bindings = RenderComponents.Values
                .Where(binding =>
                    StringComparer.OrdinalIgnoreCase.Equals(binding.Session.ModId, session.ModId) &&
                    binding.Session.Generation == session.Generation)
                .ToArray();
        }

        var operations = Volatile.Read(ref s_hostOperations);
        if (operations != null)
        {
            try
            {
                // One call clears every pointer for the MOD, so a per-binding failure cannot leave
                // some of them live.
                operations.ClearNativeRenderHosts(session.ModId);
            }
            catch (Exception exception)
            {
                Logger.Error(
                    nameof(PcCompatManagedComponentBridge),
                    $"{UnloadDebugTag} native render host clear failed mod={session.ModId}: " +
                    Unwrap(exception).Message);
            }
        }

        if (bindings.Length == 0)
            return;
        lock (Gate)
        {
            foreach (var binding in bindings)
            {
                if (RenderComponents.TryGetValue(binding.HostInstancePointer, out var current) &&
                    ReferenceEquals(current, binding))
                {
                    RenderComponents.Remove(binding.HostInstancePointer);
                }
            }
        }
        Logger.Info(
            nameof(PcCompatManagedComponentBridge),
            $"{UnloadDebugTag} render-components-cleared mod={session.ModId} " +
            $"generation={session.Generation} count={bindings.Length}");
    }

    /// <summary>
    /// Drops one component's render binding, if it has one. Called from entry destruction so a single
    /// pooled object being destroyed does not leave its pointer live in the native set.
    /// </summary>
    private static void RemoveRenderComponentBinding(ComponentEntry entry)
    {
        RenderComponentBinding? binding;
        lock (Gate)
        {
            binding = RenderComponents.Values.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Entry, entry));
            if (binding != null)
                RenderComponents.Remove(binding.HostInstancePointer);
        }
        if (binding == null)
            return;

        var operations = Volatile.Read(ref s_hostOperations);
        if (operations == null)
            return;
        try
        {
            operations.UnregisterNativeRenderHost(
                binding.Session.ModId,
                binding.HostInstancePointer);
        }
        catch (Exception exception)
        {
            Logger.Error(
                nameof(PcCompatManagedComponentBridge),
                "native render host unregister failed type=" +
                $"{entry.ComponentType.FullName}: {Unwrap(exception).Message}");
        }
    }

    private static bool TryGetEntry(object source, out ComponentEntry entry)
    {
        lock (Gate)
            return Components.TryGetValue(source, out entry!);
    }

    private static bool TryGetNativeLease(object source, out NativeObjectLease lease)
    {
        lock (Gate)
        {
            if (NativeLeases.TryGetValue(source, out lease!))
                return true;
        }

        var operations = Volatile.Read(ref s_hostOperations);
        if (operations == null)
        {
            lease = null!;
            return false;
        }

        var nativeIdentity = operations.ReadNativeInstancePointer(source);
        if (nativeIdentity == 0)
        {
            lease = null!;
            return false;
        }

        lock (Gate)
            return NativeLeasesByIdentity.TryGetValue(nativeIdentity, out lease!);
    }

    private static void MarkNativeLeaseDestroyScheduled(NativeObjectLease lease)
    {
        lock (Gate)
        {
            if (NativeLeases.TryGetValue(lease.Target, out var current) &&
                ReferenceEquals(current, lease))
            {
                lease.DestroyScheduled = true;
            }
        }
    }

    private static string? DestroyNativeLease(
        NativeObjectLease lease,
        PcCompatManagedComponentHostOperations operations)
    {
        lock (Gate)
        {
            if (!NativeLeases.TryGetValue(lease.Target, out var current) ||
                !ReferenceEquals(current, lease) ||
                lease.Destroying)
            {
                return null;
            }
            lease.Destroying = true;
        }

        try
        {
            if (!operations.IsNativeObjectAlive(lease.Target))
            {
                RetireNativeLease(lease);
                return null;
            }
            var owner = ResolveOwner(lease.OwnerGameObject);
            if (!owner.IsAlive || owner.Identity == 0 || owner.Identity != lease.OwnerIdentity)
            {
                RetireNativeLease(lease);
                return null;
            }
            operations.DestroyNativeObject(lease.Target);
            RetireNativeLease(lease);
            return null;
        }
        catch (Exception exception)
        {
            lock (Gate)
            {
                if (NativeLeases.TryGetValue(lease.Target, out var current) &&
                    ReferenceEquals(current, lease))
                {
                    lease.Destroying = false;
                }
            }
            return $"Native Unity object lease destroy failed for " +
                   $"{lease.ObjectType.AssemblyQualifiedName}: {Unwrap(exception)}";
        }
    }

    private static bool RetireNativeLease(NativeObjectLease lease)
    {
        var retired = false;
        lock (Gate)
        {
            if (!NativeLeases.TryGetValue(lease.Target, out var current) ||
                !ReferenceEquals(current, lease))
            {
                return false;
            }
            NativeLeases.Remove(lease.Target);
            if (lease.NativeIdentity != 0 &&
                NativeLeasesByIdentity.TryGetValue(lease.NativeIdentity, out var byIdentity) &&
                ReferenceEquals(byIdentity, lease))
            {
                NativeLeasesByIdentity.Remove(lease.NativeIdentity);
            }
            if (Sessions.TryGetValue(lease.Session, out var bucket))
            {
                bucket.NativeLeases.Remove(lease);
                if (bucket.IsEmpty)
                    Sessions.Remove(lease.Session);
            }
            retired = true;
        }
        if (retired)
            RetireOwnedUnityObject(lease.Session, NativeLeaseIdentity(lease));
        return retired;
    }

    private static void ScheduleDestroy(ComponentEntry entry, float delay)
    {
        lock (Gate)
        {
            if (!Components.TryGetValue(entry.Instance, out var current) ||
                !ReferenceEquals(current, entry) ||
                !Sessions.TryGetValue(entry.Session, out var bucket))
            {
                return;
            }
            var deadline = bucket.ScaledTime + delay;
            if (!double.IsFinite(deadline))
                throw new OverflowException("Managed component destroy deadline overflowed.");
            entry.ScheduleDestroy(deadline);
        }
    }

    private static ComponentEntry RequireCoroutineOwner(object source)
    {
        var execution = RequireExecutionContext();
        lock (Gate)
        {
            if (!Components.TryGetValue(source, out var entry) || entry.Destroying)
            {
                throw new InvalidOperationException(
                    "Restricted managed coroutines require a registered managed component receiver.");
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Session.ModId, execution.ModId) ||
                entry.Session.Generation != execution.ResourceSessionGeneration)
            {
                throw new InvalidOperationException(
                    "Managed coroutine receiver belongs to a different MOD session.");
            }
            return entry;
        }
    }

    private static string? DestroyEntry(ComponentEntry entry)
    {
        var execution = PcCompatManagedExecutionContext.Current;
        if (execution != null &&
            execution.Phase == PcCompatManagedExecutionPhase.Disable &&
            StringComparer.OrdinalIgnoreCase.Equals(execution.ModId, entry.Session.ModId) &&
            execution.ResourceSessionGeneration == entry.Session.Generation)
            return DestroyEntryCore(entry);

        using var disableScope = PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                entry.Session.ModId,
                entry.Session.Generation,
                PcCompatManagedExecutionPhase.Disable));
        return DestroyEntryCore(entry);
    }

    private static string? DestroyEntryCore(ComponentEntry entry)
    {
        bool demandChanged;
        lock (Gate)
        {
            if (entry.Destroying || !Components.ContainsKey(entry.Instance))
                return null;
            entry.Destroying = true;
            MarkManagedComponentRetired(entry);
            demandChanged = Sessions.TryGetValue(entry.Session, out var bucket) &&
                            PublishDispatchStateLocked(entry.Session, bucket);
        }
        if (demandChanged)
            NotifyDemandChanged();

        // Before OnDisable/OnDestroy run: a render callback arriving mid-teardown would otherwise
        // dispatch into a component whose OnDestroy has already executed.
        RemoveRenderComponentBinding(entry);

        var failures = new List<string>(2);
        if (entry.Active)
        {
            entry.Active = false;
            try
            {
                Invoke(entry, entry.OnDisable, "OnDisable");
            }
            catch (Exception exception)
            {
                failures.Add(FormatFailure(entry, "OnDisable", exception, null));
            }
        }
        try
        {
            entry.CancelCoroutines();
        }
        catch (Exception exception)
        {
            failures.Add(FormatFailure(entry, "coroutine disposal", exception, null));
        }
        try
        {
            Invoke(entry, entry.OnDestroy, "OnDestroy");
        }
        catch (Exception exception)
        {
            failures.Add(FormatFailure(entry, "OnDestroy", exception, null));
        }
        finally
        {
            lock (Gate)
            {
                Components.Remove(entry.Instance);
                if (Sessions.TryGetValue(entry.Session, out var bucket))
                {
                    bucket.Entries.Remove(entry);
                    bucket.InvalidateSnapshot();
                    if (bucket.IsEmpty)
                        Sessions.Remove(entry.Session);
                    PublishDispatchStateLocked(
                        entry.Session,
                        bucket.IsEmpty ? null : bucket);
                }
            }
            RetireOwnedUnityObject(entry.Session, ManagedComponentIdentity(entry));
        }
        return failures.Count == 0 ? null : string.Join(Environment.NewLine, failures);
    }

    private static bool TryRegisterOwnedUnityObject(
        SessionKey session,
        string identity)
        => PcCompatRuntime.TryRegisterOwnedResource(
            session.ModId,
            session.Generation,
            ModOwnedResourceKind.UnityObject,
            identity);

    private static void RetireOwnedUnityObject(
        SessionKey session,
        string identity)
        => PcCompatRuntime.RetireOwnedResource(
            session.ModId,
            session.Generation,
            ModOwnedResourceKind.UnityObject,
            identity);

    private static string ManagedComponentIdentity(ComponentEntry entry)
        => $"managed-component=0x{RuntimeHelpers.GetHashCode(entry.Instance):X};" +
           $"owner=0x{entry.OwnerIdentity:X};";

    private static string NativeLeaseIdentity(NativeObjectLease lease)
        => $"native-component=0x{RuntimeHelpers.GetHashCode(lease.Target):X};" +
           $"owner=0x{lease.OwnerIdentity:X};";

    private static PcCompatManagedComponentOwnerSnapshot ResolveOwner(object source)
    {
        object resolverSource = source;
        lock (Gate)
        {
            if (Components.TryGetValue(source, out var entry))
                resolverSource = entry.OwnerGameObject;
        }

        var resolver = Volatile.Read(ref s_ownerResolver)
                       ?? throw new InvalidOperationException(
                           "Managed component owner resolver is not installed.");
        var snapshot = resolver(resolverSource)
                       ?? throw new InvalidOperationException(
                           "Managed component owner resolver returned null.");
        ArgumentNullException.ThrowIfNull(snapshot.GameObject);
        return snapshot;
    }

    private static object ResolveLiveGameObject(
        PcCompatManagedComponentOwnerSnapshot snapshot,
        long? expectedOwnerIdentity = null)
    {
        if (!snapshot.IsAlive || snapshot.Identity == 0)
        {
            throw new InvalidOperationException(
                "Managed component owner GameObject is destroyed or has no native identity.");
        }
        if (expectedOwnerIdentity is { } expected && snapshot.Identity != expected)
        {
            throw new InvalidOperationException(
                $"Managed component owner identity changed from 0x{expected:x} " +
                $"to 0x{snapshot.Identity:x}.");
        }

        var operations = RequireHostOperations();
        var gameObject = snapshot.GameObject;
        if (!operations.IsGameObject(gameObject))
            return gameObject;

        var pointer = operations.ReadNativeInstancePointer(gameObject);
        var alive = pointer != 0 && operations.IsNativeObjectAlive(gameObject);
        if (!alive || pointer != snapshot.Identity)
        {
            throw new InvalidOperationException(
                $"Managed component owner GameObject identity mismatch expected=0x{snapshot.Identity:X} " +
                $"actual=0x{pointer:X} alive={alive}.");
        }

        // Prefer the wrapper retained by the native lease. This avoids handing a caller a fresh
        // proxy when the object was created by this MOD, while the fallback rewraps borrowed Unity
        // objects so a stale proxy cannot cross into another generated-proxy call.
        lock (Gate)
        {
            if (NativeLeasesByIdentity.TryGetValue(pointer, out var lease) &&
                operations.IsGameObject(lease.Target) &&
                operations.ReadNativeInstancePointer(lease.Target) == pointer &&
                operations.IsNativeObjectAlive(lease.Target))
            {
                return lease.Target;
            }
        }

        var normalized = operations.WrapNativeProxyPointer(gameObject.GetType(), (nint)pointer);
        if (!operations.IsGameObject(normalized) ||
            operations.ReadNativeInstancePointer(normalized) != pointer ||
            !operations.IsNativeObjectAlive(normalized))
        {
            throw new InvalidCastException(
                "Native GameObject identity was rewrapped as an invalid or mismatched proxy.");
        }
        var refreshed = (Volatile.Read(ref s_ownerResolver)
                         ?? throw new InvalidOperationException(
                             "Managed component owner resolver is not installed."))(normalized)
                        ?? throw new InvalidOperationException(
                            "Managed component owner resolver returned null for normalized GameObject.");
        if (!refreshed.IsAlive || refreshed.Identity != snapshot.Identity)
        {
            throw new InvalidOperationException(
                "Normalized GameObject no longer resolves to the original live native identity.");
        }
        return refreshed.GameObject;
    }

    private static object ResolveLiveGameObjectForSource(object source)
    {
        var expectedOwnerIdentity = TryGetEntry(source, out var entry)
            ? entry.OwnerIdentity
            : (long?)null;
        return ResolveLiveGameObject(ResolveOwner(source), expectedOwnerIdentity);
    }

    private static PcCompatManagedExecutionState RequireExecutionContext()
        => PcCompatManagedExecutionContext.Current
           ?? throw new InvalidOperationException(
               "Managed component access occurred outside an owner-scoped MOD callback.");

    private static void RequireEntryOwner(
        ComponentEntry entry,
        PcCompatManagedExecutionState execution)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Session.ModId, execution.ModId) ||
            entry.Session.Generation != execution.ResourceSessionGeneration)
        {
            throw new InvalidOperationException(
                "Managed component belongs to a different MOD session.");
        }
    }

    private static void RequireLeaseOwner(
        NativeObjectLease lease,
        PcCompatManagedExecutionState execution)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(lease.Session.ModId, execution.ModId) ||
            lease.Session.Generation != execution.ResourceSessionGeneration)
        {
            throw new InvalidOperationException(
                "Native Unity object lease belongs to a different MOD session.");
        }
        if (lease.Destroying)
            throw new InvalidOperationException("Native Unity object lease is being destroyed.");
    }

    private static PcCompatManagedExecutionState RequireCanAdd()
    {
        var execution = RequireExecutionContext();
        if (execution.Phase == PcCompatManagedExecutionPhase.Disable)
        {
            throw new InvalidOperationException(
                "Managed components cannot be added while their MOD session is disabling.");
        }
        return execution;
    }

    private static PcCompatManagedComponentHostOperations RequireHostOperations()
        => Volatile.Read(ref s_hostOperations)
           ?? throw new InvalidOperationException(
               "Managed component host operations are not installed.");

    private static void RequireUnityMain()
    {
        if (!PcCompatUnityMainExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                "Managed component access is only valid inside the verified UnityMain dispatcher.");
        }
    }

    private static void ValidateManagedComponentType(Type type)
    {
        if (type.IsAbstract || type.ContainsGenericParameters || !IsMonoBehaviour(type))
        {
            throw new InvalidOperationException(
                $"Managed component type must be a closed, concrete MonoBehaviour: {type.AssemblyQualifiedName}");
        }
        if (type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null) == null)
        {
            throw new InvalidOperationException(
                $"Managed component type has no parameterless constructor: {type.AssemblyQualifiedName}");
        }
    }

    private static void ValidateManagedComponentQueryType(Type type)
    {
        if (type.ContainsGenericParameters || !IsMonoBehaviour(type))
        {
            throw new InvalidOperationException(
                $"Managed component query type must be a closed MonoBehaviour: {type.AssemblyQualifiedName}");
        }
    }

    private static bool DestroyNativeObject(
        PcCompatManagedComponentHostOperations operations,
        object target,
        List<string> failures)
    {
        try
        {
            operations.DestroyNativeObject(target);
            return true;
        }
        catch (Exception exception)
        {
            failures.Add("Native Object.Destroy passthrough failed: " + Unwrap(exception));
            return false;
        }
    }

    private static bool RetirePersistentObject(SessionKey key, object target)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(key, out var bucket) ||
                !bucket.PersistentObjects.Remove(target))
            {
                return false;
            }
            if (bucket.IsEmpty)
                Sessions.Remove(key);
            return true;
        }
    }

    private static bool IsPersistentObjectRegistered(SessionKey key, object target)
    {
        lock (Gate)
        {
            return Sessions.TryGetValue(key, out var bucket) &&
                   bucket.PersistentObjects.Contains(target);
        }
    }

    private static void AddFailure(List<string> failures, string? failure)
    {
        if (!string.IsNullOrWhiteSpace(failure))
            failures.Add(failure);
    }

    private static void ThrowDestroyFailures(object target, IReadOnlyCollection<string> failures)
    {
        if (failures.Count == 0)
            return;
        throw new InvalidOperationException(
            $"Managed destroy bridge failed for {target.GetType().AssemblyQualifiedName}:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static bool IsMonoBehaviour(Type type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current.FullName == "UnityEngine.MonoBehaviour" &&
                current.Assembly.GetName().Name == "UnityEngine.CoreModule")
                return true;
        }
        return false;
    }

    private static string DescribeComponentInventory(
        string modId,
        long sessionGeneration,
        long frameGeneration,
        IReadOnlyList<ComponentEntry> entries)
    {
        const int maxTypes = 12;
        const int maxRepresentatives = 3;
        var registered = entries
            .Where(entry => entry.IsRegistered)
            .ToArray();
        var typeGroups = registered
            .GroupBy(entry => entry.ComponentType.FullName ?? entry.ComponentType.Name)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var typeSummary = string.Join(
            ',',
            typeGroups
                .Take(maxTypes)
                .Select(group => PcCompatDeepDebug.Sanitize(group.Key) + ':' + group.Count()));
        if (typeGroups.Length > maxTypes)
            typeSummary += $",...+{typeGroups.Length - maxTypes}";

        var representatives = string.Join(
            " | ",
            registered
                .OrderBy(entry => entry.ComponentType.FullName ?? entry.ComponentType.Name,
                    StringComparer.Ordinal)
                .ThenBy(entry => entry.DebugIdentity)
                .Take(maxRepresentatives)
                .Select(entry =>
                    $"component={PcCompatDeepDebug.DescribeObject(entry.Instance)} " +
                    $"owner=0x{entry.OwnerIdentity:X} active={entry.Active} started={entry.Started} " +
                    $"destroying={entry.Destroying} calls=[{entry.DescribeInvocationCounts()}] " +
                    $"fields=[{PcCompatDeepDebug.DescribeFields(entry.Instance, includeStatic: false)}]"));

        return $"mod={PcCompatDeepDebug.Sanitize(modId)} generation={sessionGeneration} " +
               $"frame={frameGeneration} captured={entries.Count} registered={registered.Length} " +
               $"types=[{typeSummary}] representatives=[{representatives}]";
    }

    private static void Invoke(ComponentEntry entry, Action? callback, string stage)
    {
        if (callback == null)
            return;
        var sampleKey = entry.Session.ModId + "\0" + entry.Session.Generation + "\0" +
                        (entry.ComponentType.FullName ?? entry.ComponentType.Name) + "\0" + stage;
        var sampled = PcCompatDeepDebug.ShouldSample(
            "component-lifecycle",
            sampleKey,
            out var invocation,
            first: 2,
            periodic: stage == "Update" ? 8192 : 256);
        var queryCountBefore = sampled && stage == "Update"
            ? PcCompatLegacyInputBridge.GetDiagnosticSnapshot(entry.Session.ModId).QueryCount
            : 0;
        var startedAt = sampled ? Stopwatch.GetTimestamp() : 0;
        if (sampled)
        {
            PcCompatDeepDebug.Write(
                "component-lifecycle",
                $"phase=before invocation={invocation} stage={stage} " +
                $"mod={entry.Session.ModId} generation={entry.Session.Generation} " +
                $"component={PcCompatDeepDebug.DescribeObject(entry.Instance)} owner=0x{entry.OwnerIdentity:X} " +
                $"registered={entry.IsRegistered} active={entry.Active} started={entry.Started} " +
                $"destroying={entry.Destroying} inputQueries={queryCountBefore} " +
                $"fields=[{PcCompatDeepDebug.DescribeFields(entry.Instance)}]");
        }
        try
        {
            callback();
            entry.RecordInvocation(stage);
            if (sampled)
            {
                var queryCountAfter = stage == "Update"
                    ? PcCompatLegacyInputBridge.GetDiagnosticSnapshot(entry.Session.ModId).QueryCount
                    : queryCountBefore;
                PcCompatDeepDebug.Write(
                    "component-lifecycle",
                    $"phase=after invocation={invocation} stage={stage} " +
                    $"mod={entry.Session.ModId} generation={entry.Session.Generation} " +
                    $"component={PcCompatDeepDebug.DescribeObject(entry.Instance)} " +
                    $"elapsedUs={Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds:F1} " +
                    $"inputQueriesBefore={queryCountBefore} inputQueriesAfter={queryCountAfter} " +
                    $"inputQueryDelta={queryCountAfter - queryCountBefore} " +
                    $"registered={entry.IsRegistered} active={entry.Active} started={entry.Started} " +
                    $"destroying={entry.Destroying} fields=[{PcCompatDeepDebug.DescribeFields(entry.Instance)}]");
            }
        }
        catch (Exception exception)
        {
            if (sampled)
            {
                var queryCountAfter = stage == "Update"
                    ? PcCompatLegacyInputBridge.GetDiagnosticSnapshot(entry.Session.ModId).QueryCount
                    : queryCountBefore;
                var unwrapped = Unwrap(exception);
                PcCompatDeepDebug.Write(
                    "component-lifecycle",
                    $"phase=failed invocation={invocation} stage={stage} " +
                    $"mod={entry.Session.ModId} generation={entry.Session.Generation} " +
                    $"component={PcCompatDeepDebug.DescribeObject(entry.Instance)} " +
                    $"elapsedUs={Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds:F1} " +
                    $"inputQueriesBefore={queryCountBefore} inputQueriesAfter={queryCountAfter} " +
                    $"error={unwrapped.GetType().Name}:{PcCompatDeepDebug.Sanitize(unwrapped.Message)} " +
                    $"fields=[{PcCompatDeepDebug.DescribeFields(entry.Instance)}]");
            }
            throw new InvalidOperationException(
                $"{entry.ComponentType.FullName}.{stage} failed.",
                Unwrap(exception));
        }
    }

    private static string FormatFailure(
        ComponentEntry entry,
        string stage,
        Exception exception,
        string? cleanupError)
    {
        var message =
            $"Managed component {entry.ComponentType.FullName} stage '{stage}' failed: " +
            Unwrap(exception);
        return cleanupError == null
            ? message
            : message + Environment.NewLine + "cleanup failure: " + cleanupError;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
            exception = invocation.InnerException!;
        return exception;
    }

    private readonly record struct SessionKey(string ModId, long Generation);

    private readonly record struct SessionDispatchState(
        bool HasComponents,
        bool HasOnGUIComponents);

    private static bool PublishDispatchStateLocked(
        SessionKey key,
        SessionBucket? bucket)
    {
        var current = Volatile.Read(ref s_dispatchStates);
        var hasComponents = false;
        var hasOnGUIComponents = false;
        if (bucket != null)
        {
            foreach (var entry in bucket.Entries)
            {
                if (entry.Destroying)
                    continue;
                hasComponents = true;
                hasOnGUIComponents |= entry.OnGUI != null;
            }
        }

        var next = new SessionDispatchState(
            HasComponents: hasComponents,
            HasOnGUIComponents: hasOnGUIComponents);
        if (current.TryGetValue(key, out var previous))
        {
            if (hasComponents && previous == next)
                return false;
        }
        else if (!hasComponents)
        {
            return false;
        }

        var replacement = new Dictionary<SessionKey, SessionDispatchState>(
            current.Count + 1,
            SessionKeys);
        foreach (var pair in current)
            replacement.Add(pair.Key, pair.Value);

        if (hasComponents)
        {
            replacement[key] = next;
        }
        else
        {
            replacement.Remove(key);
        }
        Volatile.Write(ref s_dispatchStates, replacement);
        return true;
    }

    private static void NotifyDemandChanged()
    {
        try
        {
            Volatile.Read(ref s_demandChanged)?.Invoke();
        }
        catch
        {
            // The published component state remains authoritative; the host reports gate failures.
        }
    }

    private sealed class SessionKeyComparer : IEqualityComparer<SessionKey>
    {
        public bool Equals(SessionKey x, SessionKey y)
            => x.Generation == y.Generation &&
               StringComparer.OrdinalIgnoreCase.Equals(x.ModId, y.ModId);

        public int GetHashCode(SessionKey obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ModId),
                obj.Generation);
    }

    private sealed class SessionBucket
    {
        private ComponentEntry[] _dispatchSnapshot = [];
        private int _version;
        private int _snapshotVersion = -1;

        public List<ComponentEntry> Entries { get; } = [];
        public List<NativeObjectLease> NativeLeases { get; } = [];
        public List<SharedPropertyContribution> SharedPropertyContributions { get; } = [];
        public HashSet<object> PersistentObjects { get; } =
            new(ReferenceEqualityComparer.Instance);
        public List<ComponentEntry> LateUpdates { get; } = [];
        public bool Dispatching { get; set; }
        public bool DispatchingOnGUI { get; set; }
        public long FrameGeneration { get; set; }
        public double ScaledTime { get; set; }
        public bool IsEmpty =>
            Entries.Count == 0 &&
            NativeLeases.Count == 0 &&
            SharedPropertyContributions.Count == 0 &&
            PersistentObjects.Count == 0;

        public void InvalidateSnapshot() => _version++;

        public ComponentEntry[] GetDispatchSnapshot()
        {
            if (_snapshotVersion == _version)
                return _dispatchSnapshot;
            _dispatchSnapshot = Entries.ToArray();
            _snapshotVersion = _version;
            if (LateUpdates.Capacity < _dispatchSnapshot.Length)
                LateUpdates.Capacity = _dispatchSnapshot.Length;
            return _dispatchSnapshot;
        }
    }

    private sealed class NativeObjectLease(
        SessionKey session,
        long ownerIdentity,
        object ownerGameObject,
        object target,
        Type objectType,
        long nativeIdentity)
    {
        public SessionKey Session { get; } = session;
        public long OwnerIdentity { get; } = ownerIdentity;
        public object OwnerGameObject { get; } = ownerGameObject;
        public object Target { get; } = target;
        public Type ObjectType { get; } = objectType;
        public long NativeIdentity { get; } = nativeIdentity;
        public bool Destroying { get; set; }
        public bool DestroyScheduled { get; set; }
    }

    /// <summary>
    /// A MOD render component bound to a host proxy component, plus the compiled call into its render
    /// override.
    /// </summary>
    /// <remarks>
    /// The delegate is built once per component. The hook fires per drop per mesh rebuild - for
    /// JipperKeyViewer that is up to 64 active drops - so per-call reflection would put
    /// <c>MethodInfo.Invoke</c> and its argument array on the render path.
    /// </remarks>
    private sealed class RenderComponentBinding
    {
        private readonly Action<object?> _invoke;
        private long _failureCount;

        public RenderComponentBinding(
            SessionKey session,
            ComponentEntry entry,
            object host,
            long hostInstancePointer,
            MethodInfo renderMethod,
            Type parameterType)
        {
            Session = session;
            Entry = entry;
            Host = host;
            HostInstancePointer = hostInstancePointer;
            ParameterType = parameterType;
            // Compiled once, to an Action<object?> that casts and calls. Two things this buys:
            // the render path never touches reflection, and CreateDelegate/Expression.Call bind a
            // protected override directly - so the MOD's method visibility never has to be rewritten,
            // which was the open question in the design's unresolved list.
            var argument = Expression.Parameter(typeof(object), "argument");
            _invoke = Expression.Lambda<Action<object?>>(
                Expression.Call(
                    Expression.Constant(entry.Instance, renderMethod.DeclaringType!),
                    renderMethod,
                    Expression.Convert(argument, parameterType)),
                argument).Compile();
        }

        public SessionKey Session { get; }
        public ComponentEntry Entry { get; }
        public object Host { get; }
        public long HostInstancePointer { get; }
        public Type ParameterType { get; }
        public long FailureCount => Interlocked.Read(ref _failureCount);

        public void Invoke(object? argument) => _invoke(argument);

        public void NoteFailure() => Interlocked.Increment(ref _failureCount);
    }


    /// <summary>
    /// One arbitrated shared property on one native object. The target is compared by reference so
    /// two proxies wrapping the same native object are two keys; that is deliberate - the registry
    /// arbitrates what each MOD's own handle writes, and the projected value is written through to
    /// native either way.
    /// </summary>
    private readonly record struct SharedPropertyKey(object Target, string Property)
    {
        public bool Equals(SharedPropertyKey other)
            => ReferenceEquals(Target, other.Target) &&
               string.Equals(Property, other.Property, StringComparison.Ordinal);

        public override int GetHashCode()
            => HashCode.Combine(
                RuntimeHelpers.GetHashCode(Target),
                StringComparer.Ordinal.GetHashCode(Property));
    }

    /// <summary>
    /// Reader/writer pair for one shared native property, plus how to compare two values of it.
    /// Values travel as <see cref="object"/> because the struct-valued ones (Vector2) are boxed
    /// generated-proxy types this assembly cannot name.
    /// </summary>
    private sealed record SharedPropertyDescriptor(
        string Name,
        Func<PcCompatManagedComponentHostOperations, object, object?> Read,
        Action<PcCompatManagedComponentHostOperations, object, object?> Write);

    private const string BehaviourEnabledProperty = "UnityEngine.Behaviour.enabled";
    private const string AnchoredPositionProperty = "UnityEngine.RectTransform.anchoredPosition";

    private static readonly SharedPropertyDescriptor BehaviourEnabledDescriptor = new(
        BehaviourEnabledProperty,
        static (operations, target) => operations.ReadNativeBehaviourEnabled(target),
        static (operations, target, value) => operations.WriteNativeBehaviourEnabled(
            target,
            value is true));

    private static readonly SharedPropertyDescriptor AnchoredPositionDescriptor = new(
        AnchoredPositionProperty,
        static (operations, target) => operations.ReadNativeAnchoredPosition(target),
        static (operations, target, value) => operations.WriteNativeAnchoredPosition(
            target,
            value ?? throw new InvalidOperationException(
                "anchoredPosition contribution lost its boxed Vector2 value.")));

    private sealed class SharedPropertyState(
        SharedPropertyKey key,
        SharedPropertyDescriptor descriptor,
        object? baseline)
    {
        public SharedPropertyKey Key { get; } = key;
        public SharedPropertyDescriptor Descriptor { get; } = descriptor;

        /// <summary>
        /// The game's own value, sampled once before the first MOD contribution. Never re-sampled:
        /// a second MOD arriving later would otherwise capture the first MOD's projected value as
        /// "original" and permanently displace the object when it restores. That is exactly the
        /// collision JipperOverlayer and CheryTools produce on the beta watermark RectTransform.
        /// </summary>
        public object? Baseline { get; } = baseline;

        public Dictionary<SessionKey, SharedPropertyContribution> Contributions { get; } =
            new(new SessionKeyComparer());
    }

    private sealed class SharedPropertyContribution(
        SessionKey session,
        SharedPropertyKey key,
        SharedPropertyDescriptor descriptor,
        object? value,
        long sequence)
    {
        public SessionKey Session { get; } = session;
        public SharedPropertyKey Key { get; } = key;
        public SharedPropertyDescriptor Descriptor { get; } = descriptor;
        public object? Value { get; set; } = value;
        public long Sequence { get; } = sequence;
    }

    private sealed class ManagedComponentObjectState
    {
        private int _alive;

        public bool IsAlive => Volatile.Read(ref _alive) != 0;

        public void MarkAlive() => Volatile.Write(ref _alive, 1);

        public void MarkRetired() => Volatile.Write(ref _alive, 0);
    }

    private sealed class ComponentEntry
    {
        private readonly Func<bool>? _enabled;
        private bool _bridgeEnabled = true;
        private readonly List<ExplicitCoroutine> _explicitCoroutines = [];
        private ManagedCoroutine? _startCoroutine;
        private long _nextCoroutineId;
        private long _awakeCount;
        private long _onEnableCount;
        private long _startCount;
        private long _updateCount;
        private long _lateUpdateCount;
        private long _onDisableCount;
        private long _onDestroyCount;
        private long _onGuiCount;
        private int _registered;

        private ComponentEntry(
            SessionKey session,
            PcCompatManagedComponentOwnerSnapshot owner,
            object instance,
            Type componentType)
        {
            Session = session;
            OwnerIdentity = owner.Identity;
            OwnerGameObject = owner.GameObject;
            Instance = instance;
            ComponentType = componentType;
            Awake = CreateMessage(instance, componentType, "Awake");
            var start = CreateStartMessage(instance, componentType);
            StartAction = start.Action;
            StartCoroutineFactory = start.CoroutineFactory;
            OnEnable = CreateMessage(instance, componentType, "OnEnable");
            Update = CreateMessage(instance, componentType, "Update");
            LateUpdate = CreateMessage(instance, componentType, "LateUpdate");
            OnDisable = CreateMessage(instance, componentType, "OnDisable");
            OnDestroy = CreateMessage(instance, componentType, "OnDestroy");
            OnGUI = CreateMessage(instance, componentType, "OnGUI");
            _enabled = CreateBooleanGetter(instance, componentType, "get_enabled");
        }

        public SessionKey Session { get; }
        public long OwnerIdentity { get; }
        public object OwnerGameObject { get; }
        public object Instance { get; }
        public Type ComponentType { get; }
        public Action? Awake { get; }
        public Action? StartAction { get; }
        public Func<IEnumerator?>? StartCoroutineFactory { get; }
        public Action? OnEnable { get; }
        public Action? Update { get; }
        public Action? LateUpdate { get; }
        public Action? OnDisable { get; }
        public Action? OnDestroy { get; }
        public Action? OnGUI { get; }
        public bool Active { get; set; }
        public bool Started { get; set; }
        public bool Destroying { get; set; }
        public bool IsRegistered => Volatile.Read(ref _registered) != 0;
        public int DebugIdentity => RuntimeHelpers.GetHashCode(Instance);
        public double? DestroyDeadline { get; private set; }
        public bool RequiresScaledClock =>
            DestroyDeadline.HasValue ||
            StartCoroutineFactory != null && (!Started || _startCoroutine != null) ||
            _explicitCoroutines.Count != 0;

        public bool ReadEnabled() => _bridgeEnabled && (_enabled?.Invoke() ?? true);

        public void MarkRegistered() => Volatile.Write(ref _registered, 1);

        public void MarkRetired() => Volatile.Write(ref _registered, 0);

        public void SetBridgeEnabled(bool enabled) => _bridgeEnabled = enabled;

        public bool IsDestroyDue(double scaledTime)
            => DestroyDeadline is { } deadline && scaledTime >= deadline;

        public void ScheduleDestroy(double deadline)
        {
            if (!DestroyDeadline.HasValue || deadline < DestroyDeadline.Value)
                DestroyDeadline = deadline;
        }

        public void RecordInvocation(string stage)
        {
            switch (stage)
            {
                case "Awake": Interlocked.Increment(ref _awakeCount); break;
                case "OnEnable": Interlocked.Increment(ref _onEnableCount); break;
                case "Start": Interlocked.Increment(ref _startCount); break;
                case "Update": Interlocked.Increment(ref _updateCount); break;
                case "LateUpdate": Interlocked.Increment(ref _lateUpdateCount); break;
                case "OnDisable": Interlocked.Increment(ref _onDisableCount); break;
                case "OnDestroy": Interlocked.Increment(ref _onDestroyCount); break;
                case "OnGUI": Interlocked.Increment(ref _onGuiCount); break;
            }
        }

        public string DescribeLifecycleBindings()
            => $"Awake:{Awake != null},OnEnable:{OnEnable != null},StartAction:{StartAction != null}," +
               $"StartCoroutine:{StartCoroutineFactory != null},Update:{Update != null}," +
               $"LateUpdate:{LateUpdate != null},OnDisable:{OnDisable != null}," +
               $"OnDestroy:{OnDestroy != null},OnGUI:{OnGUI != null},enabledGetter:{_enabled != null}";

        public string DescribeInvocationCounts()
            => $"awake={Interlocked.Read(ref _awakeCount)},enable={Interlocked.Read(ref _onEnableCount)}," +
               $"start={Interlocked.Read(ref _startCount)},update={Interlocked.Read(ref _updateCount)}," +
               $"late={Interlocked.Read(ref _lateUpdateCount)},disable={Interlocked.Read(ref _onDisableCount)}," +
               $"destroy={Interlocked.Read(ref _onDestroyCount)},ongui={Interlocked.Read(ref _onGuiCount)}";

        public PcCompatManagedComponentLifecycleEntry SnapshotLifecycle()
            => new()
            {
                TypeName = ComponentType.FullName ?? ComponentType.Name,
                Active = Active,
                Started = Started,
                Destroying = Destroying,
                AwakeCount = Interlocked.Read(ref _awakeCount),
                OnEnableCount = Interlocked.Read(ref _onEnableCount),
                StartCount = Interlocked.Read(ref _startCount),
                UpdateCount = Interlocked.Read(ref _updateCount),
                LateUpdateCount = Interlocked.Read(ref _lateUpdateCount),
                OnDisableCount = Interlocked.Read(ref _onDisableCount),
                OnDestroyCount = Interlocked.Read(ref _onDestroyCount),
                OnGuiCount = Interlocked.Read(ref _onGuiCount)
            };

        public void BeginStart(
            long frameGeneration,
            double scaledTime,
            long nowTimestamp,
            Func<object, PcCompatManagedYieldDelay?> yieldDelayResolver)
        {
            Invoke(this, StartAction, "Start");
            if (StartCoroutineFactory == null)
                return;

            IEnumerator? enumerator;
            try
            {
                enumerator = StartCoroutineFactory();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"{ComponentType.FullName}.Start coroutine factory failed.",
                    Unwrap(exception));
            }
            if (enumerator == null)
                return;
            _startCoroutine = new ManagedCoroutine(enumerator);
            AdvanceCoroutines(
                frameGeneration,
                scaledTime,
                nowTimestamp,
                yieldDelayResolver);
        }

        public void AdvanceCoroutines(
            long frameGeneration,
            double scaledTime,
            long nowTimestamp,
            Func<object, PcCompatManagedYieldDelay?> yieldDelayResolver)
        {
            var coroutine = _startCoroutine;
            if (coroutine != null)
            {
                coroutine.Advance(
                    frameGeneration,
                    scaledTime,
                    nowTimestamp,
                    yieldDelayResolver);
                if (coroutine.Completed)
                    _startCoroutine = null;
            }

            for (var index = 0; index < _explicitCoroutines.Count;)
            {
                var explicitCoroutine = _explicitCoroutines[index];
                explicitCoroutine.Advance(
                    frameGeneration,
                    scaledTime,
                    nowTimestamp,
                    yieldDelayResolver);
                var currentIndex = _explicitCoroutines.IndexOf(explicitCoroutine);
                if (currentIndex < 0)
                    continue;
                if (explicitCoroutine.Completed)
                {
                    _explicitCoroutines.RemoveAt(currentIndex);
                    index = currentIndex;
                }
                else
                {
                    index = currentIndex + 1;
                }
            }
        }

        public object StartExplicitCoroutine(
            IEnumerator routine,
            long frameGeneration,
            double scaledTime,
            long nowTimestamp,
            Func<object, PcCompatManagedYieldDelay?> yieldDelayResolver,
            string? methodName = null)
        {
            var registration = new ExplicitCoroutine(
                this,
                checked(++_nextCoroutineId),
                routine,
                methodName);
            _explicitCoroutines.Add(registration);
            try
            {
                registration.Advance(
                    frameGeneration,
                    scaledTime,
                    nowTimestamp,
                    yieldDelayResolver);
                if (registration.Completed)
                    _explicitCoroutines.Remove(registration);
                return registration.Handle;
            }
            catch
            {
                _explicitCoroutines.Remove(registration);
                registration.Cancel();
                throw;
            }
        }

        public object StartNamedCoroutine(
            string methodName,
            object? value,
            bool hasArgument,
            long frameGeneration,
            double scaledTime,
            long nowTimestamp,
            Func<object, PcCompatManagedYieldDelay?> yieldDelayResolver)
        {
            var parameterCount = hasArgument ? 1 : 0;
            MethodInfo? method = null;
            for (var current = ComponentType;
                 current != null && current != typeof(object);
                 current = current.BaseType)
            {
                var candidates = current.GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Where(candidate =>
                        candidate.Name == methodName &&
                        candidate.GetParameters().Length == parameterCount)
                    .ToArray();
                if (candidates.Length == 0)
                    continue;
                if (candidates.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Named coroutine {ComponentType.FullName}.{methodName} has " +
                        $"{candidates.Length} matching overloads.");
                }
                method = candidates[0];
                break;
            }
            if (method == null || !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
            {
                throw new MissingMethodException(
                    ComponentType.FullName,
                    $"{methodName}({(hasArgument ? "object" : string.Empty)}):IEnumerator");
            }

            IEnumerator routine;
            try
            {
                routine = (IEnumerator?)method.Invoke(
                              Instance,
                              hasArgument ? [value] : null)
                          ?? throw new InvalidOperationException(
                              $"Named coroutine {ComponentType.FullName}.{methodName} returned null.");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Named coroutine {ComponentType.FullName}.{methodName} factory failed.",
                    Unwrap(exception));
            }
            return StartExplicitCoroutine(
                routine,
                frameGeneration,
                scaledTime,
                nowTimestamp,
                yieldDelayResolver,
                methodName);
        }

        public void StopExplicitCoroutine(IEnumerator routine)
        {
            var registration = _explicitCoroutines.FirstOrDefault(item =>
                ReferenceEquals(item.RootEnumerator, routine));
            if (registration == null)
                return;
            _explicitCoroutines.Remove(registration);
            registration.Cancel();
        }

        public void StopExplicitCoroutine(object handle)
        {
            if (handle is not ManagedCoroutineHandle managedHandle ||
                !ReferenceEquals(managedHandle.Owner, this))
            {
                throw new InvalidOperationException(
                    "Coroutine handle does not belong to this managed component.");
            }
            var registration = _explicitCoroutines.FirstOrDefault(item =>
                item.Id == managedHandle.Id);
            if (registration == null)
                return;
            _explicitCoroutines.Remove(registration);
            registration.Cancel();
        }

        public void StopExplicitCoroutines(string methodName)
        {
            foreach (var registration in _explicitCoroutines
                         .Where(item => string.Equals(
                             item.MethodName,
                             methodName,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                _explicitCoroutines.Remove(registration);
                registration.Cancel();
            }
        }

        public void CancelCoroutines()
        {
            var coroutine = _startCoroutine;
            _startCoroutine = null;
            coroutine?.Dispose();
            foreach (var explicitCoroutine in _explicitCoroutines.ToArray())
                explicitCoroutine.Cancel();
            _explicitCoroutines.Clear();
        }

        public static ComponentEntry Create(
            SessionKey session,
            PcCompatManagedComponentOwnerSnapshot owner,
            object instance,
            Type componentType)
            => new(session, owner, instance, componentType);

        private static StartMessage CreateStartMessage(object instance, Type componentType)
        {
            for (var current = componentType;
                 current != null && current != typeof(object);
                 current = current.BaseType)
            {
                var methods = current.GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Where(method => method.Name == "Start" && method.GetParameters().Length == 0)
                    .ToArray();
                if (methods.Length == 0)
                    continue;
                if (methods.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Managed component Start must resolve to one method: " +
                        componentType.AssemblyQualifiedName);
                }
                var method = methods[0];
                if (method.ReturnType == typeof(void))
                    return new StartMessage(method.CreateDelegate<Action>(instance), null);
                if (typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
                {
                    return new StartMessage(
                        null,
                        () => (IEnumerator?)method.Invoke(instance, null));
                }
                throw new InvalidOperationException(
                    $"Managed component Start must return void or IEnumerator: {method}");
            }
            return default;
        }

        private static Action? CreateMessage(
            object instance,
            Type componentType,
            string methodName)
        {
            for (var current = componentType;
                 current != null && current != typeof(object);
                 current = current.BaseType)
            {
                var methods = current.GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Where(method => method.Name == methodName && method.GetParameters().Length == 0)
                    .ToArray();
                if (methods.Length == 0)
                    continue;
                if (methods.Length != 1 || methods[0].ReturnType != typeof(void))
                {
                    throw new InvalidOperationException(
                        $"Managed component message must resolve to one void {methodName}(): " +
                        componentType.AssemblyQualifiedName);
                }
                return methods[0].CreateDelegate<Action>(instance);
            }
            return null;
        }

        private static Func<bool>? CreateBooleanGetter(
            object instance,
            Type componentType,
            string methodName)
        {
            var method = componentType.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);
            if (method == null)
                return null;
            if (method.ReturnType != typeof(bool))
            {
                throw new InvalidOperationException(
                    $"Managed component enabled getter has an invalid signature: {method}");
            }
            if (!IsModManagedImplementation(method, componentType))
                return null;
            return method.CreateDelegate<Func<bool>>(instance);
        }

        private static bool IsModManagedImplementation(MethodInfo method, Type componentType)
        {
            // Generated proxy members (e.g. UnityEngine.Behaviour.get_enabled) invoke native
            // code and require a real engine object. Surrogate managed components only wrap a
            // raw il2cpp_object_new allocation, so invoking them raises an IL2CPP exception.
            // Only bind members implemented by MOD-managed code; anything else keeps the
            // managed default (enabled=true, matching Unity's default for new components).
            var declaringType = method.DeclaringType;
            if (declaringType == null)
                return false;
            // Generated proxy types declare a static NativeClassPtr field; MOD types do not.
            if (declaringType.GetField(
                    "NativeClassPtr",
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly) != null)
                return false;
            // Proxies live in the default AssemblyLoadContext while MOD assemblies load into
            // the per-MOD collectible context; a foreign context means non-MOD code.
            return AssemblyLoadContext.GetLoadContext(declaringType.Assembly) ==
                   AssemblyLoadContext.GetLoadContext(componentType.Assembly);
        }

        private readonly record struct StartMessage(
            Action? Action,
            Func<IEnumerator?>? CoroutineFactory);

        private sealed class ManagedCoroutineHandle(ComponentEntry owner, long id)
        {
            public ComponentEntry Owner { get; } = owner;
            public long Id { get; } = id;
        }

        private sealed class ExplicitCoroutine(
            ComponentEntry owner,
            long id,
            IEnumerator rootEnumerator,
            string? methodName)
        {
            private readonly ManagedCoroutine _coroutine = new(rootEnumerator);
            private bool _advancing;
            private bool _cancelled;

            public long Id { get; } = id;
            public IEnumerator RootEnumerator { get; } = rootEnumerator;
            public string? MethodName { get; } = methodName;
            public object Handle { get; } = new ManagedCoroutineHandle(owner, id);
            public bool Completed => _cancelled || _coroutine.Completed;

            public void Advance(
                long frameGeneration,
                double scaledTime,
                long nowTimestamp,
                Func<object, PcCompatManagedYieldDelay?> yieldDelayResolver)
            {
                if (_cancelled)
                    return;
                _advancing = true;
                try
                {
                    _coroutine.Advance(
                        frameGeneration,
                        scaledTime,
                        nowTimestamp,
                        yieldDelayResolver);
                }
                finally
                {
                    _advancing = false;
                    if (_cancelled)
                        _coroutine.Dispose();
                }
            }

            public void Cancel()
            {
                if (_cancelled)
                    return;
                _cancelled = true;
                if (!_advancing)
                    _coroutine.Dispose();
            }
        }

        private sealed class ManagedCoroutine : IDisposable
        {
            private const int MaxNestedDepth = 32;
            private const int MaxTransitionsPerAdvance = 256;
            private readonly Stack<IEnumerator> _enumerators = new();
            private CoroutineWaitKind _waitKind;
            private long _resumeFrame;
            private double _scaledDeadline;
            private long _realtimeDeadline;
            private bool _disposed;

            public ManagedCoroutine(IEnumerator enumerator)
            {
                ArgumentNullException.ThrowIfNull(enumerator);
                _enumerators.Push(enumerator);
            }

            public bool Completed { get; private set; }

            public void Advance(
                long frameGeneration,
                double scaledTime,
                long nowTimestamp,
                Func<object, PcCompatManagedYieldDelay?> yieldDelayResolver)
            {
                if (Completed || !CanResume(frameGeneration, scaledTime, nowTimestamp))
                    return;
                _waitKind = CoroutineWaitKind.None;

                try
                {
                    for (var transitions = 0;
                         transitions < MaxTransitionsPerAdvance;
                         transitions++)
                    {
                        var current = _enumerators.Peek();
                        if (!current.MoveNext())
                        {
                            PopAndDispose();
                            if (_enumerators.Count == 0)
                            {
                                Completed = true;
                                return;
                            }
                            continue;
                        }

                        var yielded = current.Current;
                        if (yielded is IEnumerator nested)
                        {
                            if (_enumerators.Count >= MaxNestedDepth)
                            {
                                throw new NotSupportedException(
                                    $"Managed coroutine nesting exceeds {MaxNestedDepth} enumerators.");
                            }
                            if (_enumerators.Any(active => ReferenceEquals(active, nested)))
                            {
                                throw new InvalidOperationException(
                                    "Managed coroutine yielded an enumerator already active in its stack.");
                            }
                            _enumerators.Push(nested);
                            continue;
                        }

                        _resumeFrame = checked(frameGeneration + 1);
                        if (yielded == null)
                        {
                            _waitKind = CoroutineWaitKind.NextFrame;
                            return;
                        }

                        var delay = yieldDelayResolver(yielded)
                                    ?? throw new NotSupportedException(
                                        $"Unsupported managed coroutine yield instruction: " +
                                        yielded.GetType().AssemblyQualifiedName);
                        if (!float.IsFinite(delay.Seconds) || delay.Seconds < 0f)
                        {
                            throw new InvalidOperationException(
                                $"Invalid managed coroutine delay: {delay.Kind} {delay.Seconds}.");
                        }
                        switch (delay.Kind)
                        {
                            case PcCompatManagedYieldDelayKind.ScaledSeconds:
                                _waitKind = CoroutineWaitKind.ScaledDeadline;
                                _scaledDeadline = scaledTime + delay.Seconds;
                                return;
                            case PcCompatManagedYieldDelayKind.RealtimeSeconds:
                                _waitKind = CoroutineWaitKind.RealtimeDeadline;
                                var delayTicks = Math.Ceiling(delay.Seconds * Stopwatch.Frequency);
                                if (delayTicks > long.MaxValue - nowTimestamp)
                                {
                                    throw new OverflowException(
                                        "Managed coroutine realtime deadline overflowed.");
                                }
                                _realtimeDeadline = nowTimestamp + (long)delayTicks;
                                return;
                            default:
                                throw new NotSupportedException(
                                    $"Unsupported managed coroutine delay kind: {delay.Kind}.");
                        }
                    }

                    throw new InvalidOperationException(
                        $"Managed coroutine exceeded {MaxTransitionsPerAdvance} immediate transitions " +
                        "in one dispatcher opportunity.");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                while (_enumerators.Count != 0)
                    PopAndDispose();
            }

            private void PopAndDispose()
            {
                var enumerator = _enumerators.Pop();
                if (enumerator is IDisposable disposable)
                    disposable.Dispose();
            }

            private bool CanResume(long frameGeneration, double scaledTime, long nowTimestamp)
            {
                if (frameGeneration < _resumeFrame)
                    return false;
                return _waitKind switch
                {
                    CoroutineWaitKind.None => true,
                    CoroutineWaitKind.NextFrame => true,
                    CoroutineWaitKind.ScaledDeadline => scaledTime >= _scaledDeadline,
                    CoroutineWaitKind.RealtimeDeadline => nowTimestamp >= _realtimeDeadline,
                    _ => false
                };
            }

            private enum CoroutineWaitKind
            {
                None,
                NextFrame,
                ScaledDeadline,
                RealtimeDeadline
            }
        }
    }
}

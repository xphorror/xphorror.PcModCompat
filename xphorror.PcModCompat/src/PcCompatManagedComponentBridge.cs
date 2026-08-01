using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

public sealed record PcCompatManagedComponentOwnerSnapshot(
    long Identity,
    object GameObject,
    bool IsAlive,
    bool IsActive);

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
    Action<object> DontDestroyNativeObject,
    Action<object> DestroyNativeObject,
    Action<object, float> DestroyNativeObjectDelayed,
    Func<object, PcCompatManagedYieldDelay?> ResolveYieldDelay,
    Func<float> ReadScaledDeltaTime);

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
    private static readonly Dictionary<SessionKey, SessionBucket> Sessions =
        new(new SessionKeyComparer());
    private static readonly Dictionary<object, ComponentEntry> Components =
        new(ReferenceEqualityComparer.Instance);
    private static Func<object, PcCompatManagedComponentOwnerSnapshot>? s_ownerResolver;
    private static PcCompatManagedComponentHostOperations? s_hostOperations;

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

    public static T AddComponent<T>(object owner)
        => (T)AddManagedComponent(owner, typeof(T));

    public static object AddComponent(object owner, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(componentType);
        RequireUnityMain();
        RequireCanAdd();
        var operations = RequireHostOperations();
        if (!operations.IsNativeComponentType(componentType))
            return AddManagedComponent(owner, componentType);

        var ownerSnapshot = ResolveOwner(owner);
        if (!ownerSnapshot.IsAlive || ownerSnapshot.Identity == 0)
        {
            throw new InvalidOperationException(
                $"Cannot add native component {componentType.FullName} to a destroyed owner.");
        }
        return operations.AddNativeComponent(ownerSnapshot.GameObject, componentType)
               ?? throw new InvalidOperationException(
                   $"Native AddComponent returned null for {componentType.AssemblyQualifiedName}.");
    }

    private static object AddManagedComponent(object owner, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireUnityMain();
        var execution = RequireCanAdd();
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

    public static T? GetComponent<T>(object owner)
        => (T?)GetManagedComponent(owner, typeof(T));

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
        return operations.GetNativeComponent(source, requestedType);
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
        return operations.GetNativeComponents(source, requestedType)
               ?? throw new InvalidOperationException(
                   $"Native GetComponents returned null for {requestedType.AssemblyQualifiedName}.");
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
        return ResolveOwner(source).GameObject;
    }

    public static object GetTransform(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        RequireExecutionContext();
        var owner = ResolveOwner(source);
        if (!owner.IsAlive || owner.Identity == 0)
            throw new InvalidOperationException("Cannot resolve transform for a destroyed owner.");
        return RequireHostOperations().ResolveTransform(owner.GameObject)
               ?? throw new InvalidOperationException("Managed component host returned a null transform.");
    }

    public static bool GetEnabled(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        if (!TryGetEntry(source, out var entry))
            return RequireHostOperations().ReadNativeBehaviourEnabled(source);

        RequireEntryOwner(entry, execution);
        return !entry.Destroying && entry.ReadEnabled();
    }

    public static void SetEnabled(object source, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireUnityMain();
        var execution = RequireExecutionContext();
        if (!TryGetEntry(source, out var entry))
        {
            RequireHostOperations().WriteNativeBehaviourEnabled(source, enabled);
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

        if (operations.IsGameObject(target))
        {
            var owner = ResolveOwner(target);
            ComponentEntry[] entries;
            lock (Gate)
            {
                entries = Components.Values
                    .Where(entry => !entry.Destroying && entry.OwnerIdentity == owner.Identity)
                    .ToArray();
            }
            foreach (var entry in entries)
                AddFailure(failures, DestroyEntry(entry));
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
        RequireExecutionContext();
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

        operations.DestroyNativeObjectDelayed(target, delay);
        if (!operations.IsGameObject(target))
            return;

        var owner = ResolveOwner(target);
        ComponentEntry[] entries;
        lock (Gate)
        {
            entries = Components.Values
                .Where(entry => !entry.Destroying && entry.OwnerIdentity == owner.Identity)
                .ToArray();
        }
        foreach (var entry in entries)
            ScheduleDestroy(entry, delay);
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
        lock (Gate)
        {
            return Sessions.TryGetValue(
                       new SessionKey(modId, sessionGeneration),
                       out var bucket) &&
                   bucket.Entries.Any(entry => !entry.Destroying);
        }
    }

    public static bool HasOnGUIComponents(string modId, long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            return Sessions.TryGetValue(
                       new SessionKey(modId, sessionGeneration),
                       out var bucket) &&
                   bucket.Entries.Any(entry => !entry.Destroying && entry.OnGUI != null);
        }
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
        ComponentEntry[] entries;
        object[] persistentObjects;
        var key = new SessionKey(modId, sessionGeneration);
        var empty = false;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(key, out var bucket) ||
                (bucket.Entries.Count == 0 && bucket.PersistentObjects.Count == 0))
            {
                empty = true;
                entries = [];
                persistentObjects = [];
            }
            else
            {
                entries = bucket.Entries.ToArray();
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
            $"entries={entries.Length} persistent={persistentObjects.Length} " +
            $"unityMain={PcCompatUnityMainExecutionContext.IsActive} " +
            $"tid={Environment.CurrentManagedThreadId}");

        if (!PcCompatUnityMainExecutionContext.IsActive)
        {
            lock (Gate)
            {
                foreach (var entry in entries)
                    Components.Remove(entry.Instance);
                Sessions.Remove(key);
            }
            error =
                "Managed component cleanup occurred outside UnityMain; entries and persistent objects " +
                "were detached without callbacks.";
            Logger.Error(
                nameof(PcCompatManagedComponentBridge),
                $"{UnloadDebugTag} session-clear-detached mod={key.ModId} generation={key.Generation} " +
                $"entries={entries.Length} persistent={persistentObjects.Length} " +
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
                bucket.Entries.Count == 0 && bucket.PersistentObjects.Count == 0)
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
        }
    }

    private static bool IsRegistered(ComponentEntry entry)
    {
        lock (Gate)
            return Components.TryGetValue(entry.Instance, out var current) && ReferenceEquals(current, entry);
    }

    private static bool TryGetEntry(object source, out ComponentEntry entry)
    {
        lock (Gate)
            return Components.TryGetValue(source, out entry!);
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
        lock (Gate)
        {
            if (entry.Destroying || !Components.ContainsKey(entry.Instance))
                return null;
            entry.Destroying = true;
        }

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
                    if (bucket.Entries.Count == 0 && bucket.PersistentObjects.Count == 0)
                        Sessions.Remove(entry.Session);
                }
            }
        }
        return failures.Count == 0 ? null : string.Join(Environment.NewLine, failures);
    }

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
            if (bucket.Entries.Count == 0 && bucket.PersistentObjects.Count == 0)
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

    private static void Invoke(ComponentEntry entry, Action? callback, string stage)
    {
        if (callback == null)
            return;
        try
        {
            callback();
            entry.RecordInvocation(stage);
        }
        catch (Exception exception)
        {
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
        public HashSet<object> PersistentObjects { get; } =
            new(ReferenceEqualityComparer.Instance);
        public List<ComponentEntry> LateUpdates { get; } = [];
        public bool Dispatching { get; set; }
        public bool DispatchingOnGUI { get; set; }
        public long FrameGeneration { get; set; }
        public double ScaledTime { get; set; }

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
        public double? DestroyDeadline { get; private set; }
        public bool RequiresScaledClock =>
            DestroyDeadline.HasValue ||
            StartCoroutineFactory != null && (!Started || _startCoroutine != null) ||
            _explicitCoroutines.Count != 0;

        public bool ReadEnabled() => _bridgeEnabled && (_enabled?.Invoke() ?? true);

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

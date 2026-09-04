using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace StArray.ModManager.Runtime;

public enum ModDataDomainLoaderKind : uint
{
    Unknown = 0,
    AndroidManaged = 1,
    PcCompat = 2,
    Host = 3,
    Other = 255
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct ModDataDomainToken : IEquatable<ModDataDomainToken>
{
    internal ModDataDomainToken(
        ulong processCookie,
        long generation,
        int slotIndex,
        ModDataDomainLoaderKind loaderKind)
    {
        ProcessCookie = processCookie;
        Generation = generation;
        SlotIndex = slotIndex;
        LoaderKind = loaderKind;
    }

    public ulong ProcessCookie { get; }
    public long Generation { get; }
    public int SlotIndex { get; }
    public ModDataDomainLoaderKind LoaderKind { get; }

    public bool IsValid =>
        ProcessCookie != 0 &&
        Generation > 0 &&
        SlotIndex >= 0 &&
        LoaderKind != ModDataDomainLoaderKind.Unknown;

    public bool Equals(ModDataDomainToken other) =>
        ProcessCookie == other.ProcessCookie &&
        Generation == other.Generation &&
        SlotIndex == other.SlotIndex &&
        LoaderKind == other.LoaderKind;

    public override bool Equals(object? obj) =>
        obj is ModDataDomainToken other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(ProcessCookie, Generation, SlotIndex, LoaderKind);

    public static bool operator ==(ModDataDomainToken left, ModDataDomainToken right) =>
        left.Equals(right);

    public static bool operator !=(ModDataDomainToken left, ModDataDomainToken right) =>
        !left.Equals(right);

    public override string ToString() =>
        IsValid
            ? $"slot={SlotIndex};generation={Generation};loader={LoaderKind}"
            : "<invalid-domain-token>";
}

/// <summary>
/// Stable contract used by rewritten MOD assemblies. Mutable static state is selected from the
/// active domain instead of a process-global backing field.
/// </summary>
public static class ModDataDomainRuntime
{
    private static readonly AsyncLocal<ModDataDomain?> Current = new();

    public static ModDataDomainToken CurrentToken =>
        TryGetCurrent(out var domain) ? domain.Token : default;

    public static T GetOrCreateStaticSlot<T>(int slotId, Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return RequireCurrent().GetOrCreateStaticSlot(slotId, factory);
    }

    public static bool TryGetStaticSlot<T>(int slotId, out T? value) =>
        RequireCurrent().TryGetStaticSlot(slotId, out value);

    public static void SetStaticSlot<T>(int slotId, T value) =>
        RequireCurrent().SetStaticSlot(slotId, value);

    public static T GetStaticSlot<T>(int slotId) =>
        RequireCurrent().GetStaticSlot<T>(slotId);

    public static ref T GetStaticSlotReference<T>(int slotId) =>
        ref RequireCurrent().GetStaticSlotReference<T>(slotId);

    /// <summary>
    /// Gets a mutable static slot whose identity includes the closed generic owner type.
    /// Generic MOD static fields must use this overload so <c>Foo&lt;A&gt;</c> and
    /// <c>Foo&lt;B&gt;</c> cannot share state merely because their field signatures match.
    /// </summary>
    public static T GetStaticSlotForOwner<T, TOwner>(int slotId) =>
        RequireCurrent().GetStaticSlotForOwner<T, TOwner>(slotId);

    public static void SetStaticSlotForOwner<T, TOwner>(int slotId, T value) =>
        RequireCurrent().SetStaticSlotForOwner<T, TOwner>(slotId, value);

    public static ref T GetStaticSlotReferenceForOwner<T, TOwner>(int slotId) =>
        ref RequireCurrent().GetStaticSlotReferenceForOwner<T, TOwner>(slotId);

    public static void EnsureStaticTypeInitialized(
        int typeId,
        RuntimeMethodHandle initializerHandle) =>
        RequireCurrent().EnsureStaticTypeInitialized(
            typeId,
            initializerHandle,
            default);

    public static void EnsureStaticTypeInitialized(
        int typeId,
        RuntimeMethodHandle initializerHandle,
        RuntimeTypeHandle ownerTypeHandle) =>
        RequireCurrent().EnsureStaticTypeInitialized(
            typeId,
            initializerHandle,
            ownerTypeHandle);

    public static void RequireCurrentDomain() => _ = RequireCurrent();

    internal static IDisposable EnterScope(ModDataDomainToken token)
    {
        if (!ModDataDomainRegistry.TryResolve(token, out var domain))
            throw new InvalidOperationException($"MOD data domain token is stale: {token}.");

        var previous = Current.Value;
        Current.Value = domain;
        return new Scope(previous);
    }

    internal static IDisposable SuppressScope()
    {
        var previous = Current.Value;
        Current.Value = null;
        return new Scope(previous);
    }

    internal static bool TryEnterCallback(
        ModDataDomainToken token,
        out IDisposable? lease)
    {
        lease = null;
        if (!ModDataDomainRegistry.TryResolve(token, out var domain) ||
            !domain.Session.TryEnterCallback(domain.Key, out var callbackLease))
        {
            return false;
        }

        try
        {
            var scope = EnterScope(token);
            lease = new CallbackScopeLease(callbackLease!, scope);
            return true;
        }
        catch
        {
            callbackLease?.Dispose();
            throw;
        }
    }

    private static ModDataDomain RequireCurrent() =>
        TryGetCurrent(out var domain)
            ? domain
            : throw new InvalidOperationException(
                "An active MOD data domain is required for this operation.");

    private static bool TryGetCurrent(out ModDataDomain domain)
    {
        var candidate = Current.Value;
        if (candidate != null &&
            ModDataDomainRegistry.TryResolve(candidate.Token, out var resolved) &&
            ReferenceEquals(candidate, resolved))
        {
            domain = candidate;
            return true;
        }
        domain = null!;
        return false;
    }

    private sealed class Scope(ModDataDomain? previous) : IDisposable
    {
        private ModDataDomain? _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Current.Value = _previous;
            _previous = null;
        }
    }

    private sealed class CallbackScopeLease(
        IDisposable callbackLease,
        IDisposable scope) : IDisposable
    {
        private IDisposable? _callbackLease = callbackLease;
        private IDisposable? _scope = scope;

        public void Dispose()
        {
            Interlocked.Exchange(ref _scope, null)?.Dispose();
            Interlocked.Exchange(ref _callbackLease, null)?.Dispose();
        }
    }
}

internal static class ModDataDomainPaths
{
    /// <summary>
    /// Whether <paramref name="fullPath"/> is <paramref name="root"/> itself or lives under
    /// it. Both arguments must already be normalized absolute paths.
    /// </summary>
    /// <remarks>
    /// Compares on a separator boundary so "/data/mods/AB" is not treated as living under
    /// "/data/mods/A". Android paths are case-sensitive, but the shadow/config roots are
    /// Host-generated and also compared case-insensitively so a Windows host test run and
    /// the device agree on containment.
    /// </remarks>
    internal static bool IsWithin(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
            return false;
        if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        var next = fullPath[root.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Whether any existing component of <paramref name="fullPath"/> below
    /// <paramref name="root"/> is a symlink or reparse point (junction).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsWithin"/> is purely lexical, and <see cref="Path.GetFullPath(string)"/>
    /// only normalizes text — neither follows links. A MOD that creates a junction inside its
    /// own writable root (no elevation required on Windows, and
    /// <c>Directory.CreateSymbolicLink</c> is plain BCL) would otherwise reach another MOD's
    /// roots, the game directory or system paths while every ownership check still sees a
    /// legitimate prefix. MOD_RUNTIME_ISOLATION/§4.10 requires this traversal to fail closed.
    /// </para>
    /// <para>
    /// Only components that already exist are inspected: a path being created cannot be a
    /// link yet, and its parents are covered. The walk stops at the root, so Host-owned links
    /// above it (an emulated-storage path, a relocated data directory) are not the MOD's doing
    /// and must not fail its access.
    /// </para>
    /// </remarks>
    internal static bool TraversesLinkBelow(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
            return false;

        var current = fullPath;
        while (!string.IsNullOrEmpty(current) &&
               !current.Equals(root, StringComparison.OrdinalIgnoreCase) &&
               IsWithin(root, current))
        {
            if (HasReparsePoint(current))
                return true;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }
        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            // LinkTarget is non-null exactly for symlinks and reparse points/junctions, and
            // is populated without following the link.
            var info = Directory.Exists(path)
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path);
            if (!info.Exists)
                return false;
            return info.LinkTarget != null ||
                   info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Fail closed: a component that cannot be inspected must not be assumed safe.
            return true;
        }
    }
}

/// <summary>
/// Platform-provided filesystem policy for Android managed MODs. Roots come from the
/// host platform, not from MOD identities or hard-coded application directories.
/// </summary>
public sealed record ModHostPathPolicy
{
    public IReadOnlyList<string> SharedReadOnlyRoots { get; init; } = [];
    public IReadOnlyList<string> SharedWritableRoots { get; init; } = [];
    public IReadOnlyList<string> HostProtectedRoots { get; init; } = [];
}

internal sealed record ModDataDomainPathRoots
{
    /// <summary>MOD original directory. Read-only package layer: execution uses the shadow package.</summary>
    public required string InstallRoot { get; init; }

    public required string ConfigRoot { get; init; }
    public required string CacheRoot { get; init; }
    public required string LogRoot { get; init; }
    public required string TempRoot { get; init; }

    /// <summary>
    /// Owner-scoped VFS overlay: writes aimed at the install root land here with the same
    /// relative layout and reads prefer it over the package layer, so legacy data files stay
    /// readable without a migration copy. Executable files are excluded — replacing the MOD's
    /// own binaries stays fail-closed because package contents are Host-owned.
    /// </summary>
    public required string DataOverlayRoot { get; init; }

    /// <summary>
    /// Official game resource directories. Shared read-only facts: readable by any
    /// domain, never writable through the bridge.
    /// </summary>
    public IReadOnlyList<string> SharedReadOnlyRoots { get; init; } = [];

    /// <summary>
    /// Host-granted platform storage. Android supplies these roots from platform APIs;
    /// the path bridge does not infer permissions from MOD IDs or path names.
    /// </summary>
    public IReadOnlyList<string> SharedWritableRoots { get; init; } = [];

    /// <summary>
    /// Host-owned subtrees nested inside shared storage. A current MOD's explicit private
    /// roots still take precedence, while every other access into these trees is denied.
    /// </summary>
    public IReadOnlyList<string> HostProtectedRoots { get; init; } = [];

    internal IEnumerable<string> WritableRoots
    {
        get
        {
            yield return ConfigRoot;
            yield return CacheRoot;
            yield return LogRoot;
            yield return TempRoot;
            // The overlay is a writable root in its own right: merged enumeration and
            // GetFullPath hand shadow paths back to the MOD, and those must remain usable
            // through the bridge instead of falling outside every root and being denied.
            yield return DataOverlayRoot;
        }
    }

    internal IEnumerable<string> OwnedRoots
    {
        get
        {
            yield return InstallRoot;
            foreach (var root in WritableRoots)
                yield return root;
        }
    }
}

internal sealed class ModDataDomain
{
    private readonly object _domainSync = new();
    private readonly ConcurrentDictionary<StaticSlotKey, IStaticSlotCell> _staticSlots = new();
    private readonly Dictionary<StaticInitializerKey, StaticTypeInitialization>
        _staticTypeInitializers = new();
    private IReadOnlyDictionary<string, string> _originalAssemblyLocations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private ModDataDomainPathRoots? _pathRoots;
    private ModRuntimeNetworkBridge.DomainNetworkState? _networkState;

    internal ModDataDomain(
        ModRuntimeKey key,
        ModDataDomainToken token,
        ModRuntimeSession session)
    {
        Key = key;
        Token = token;
        Session = session;
    }

    internal ModRuntimeKey Key { get; }
    internal ModDataDomainToken Token { get; }
    internal ModRuntimeSession Session { get; }

    internal void BindOriginalAssemblyLocations(IReadOnlyDictionary<string, string> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        lock (_domainSync)
        {
            _originalAssemblyLocations = locations.ToDictionary(
                pair => pair.Key,
                pair => Path.GetFullPath(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    internal bool TryGetOriginalAssemblyLocation(string assemblyName, out string path)
    {
        lock (_domainSync)
            return _originalAssemblyLocations.TryGetValue(assemblyName, out path!);
    }

    /// <summary>
    /// Binds the per-domain filesystem roots. Rebound on every generation so a
    /// reloaded MOD cannot keep writing through roots captured by the old one.
    /// </summary>
    internal void BindPathRoots(ModDataDomainPathRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = new ModDataDomainPathRoots
        {
            InstallRoot = NormalizeRoot(roots.InstallRoot, nameof(roots.InstallRoot)),
            ConfigRoot = NormalizeRoot(roots.ConfigRoot, nameof(roots.ConfigRoot)),
            CacheRoot = NormalizeRoot(roots.CacheRoot, nameof(roots.CacheRoot)),
            LogRoot = NormalizeRoot(roots.LogRoot, nameof(roots.LogRoot)),
            TempRoot = NormalizeRoot(roots.TempRoot, nameof(roots.TempRoot)),
            DataOverlayRoot = NormalizeRoot(roots.DataOverlayRoot, nameof(roots.DataOverlayRoot)),
            SharedReadOnlyRoots = roots.SharedReadOnlyRoots
                .Select(root => NormalizeRoot(root, nameof(roots.SharedReadOnlyRoots)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SharedWritableRoots = roots.SharedWritableRoots
                .Select(root => NormalizeRoot(root, nameof(roots.SharedWritableRoots)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            HostProtectedRoots = roots.HostProtectedRoots
                .Select(root => NormalizeRoot(root, nameof(roots.HostProtectedRoots)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        // The path bridge may receive a temp/config path before the MOD has
        // explicitly created its first directory. Establish every host-owned
        // writable root as part of domain binding so GetTempPath plus a direct
        // FileStream create has the same usable-root contract as PcCompat.
        foreach (var writableRoot in normalized.WritableRoots)
            Directory.CreateDirectory(writableRoot);

        lock (_domainSync)
            _pathRoots = normalized;
    }

    internal bool TryGetPathRoots(out ModDataDomainPathRoots roots)
    {
        lock (_domainSync)
        {
            roots = _pathRoots!;
            return _pathRoots != null;
        }
    }

    /// <summary>
    /// Per-domain network identity (cookie jar, owned HTTP clients). Created on first use so a
    /// MOD that never touches the network pays nothing.
    /// </summary>
    internal ModRuntimeNetworkBridge.DomainNetworkState GetOrCreateNetworkState(
        Func<ModRuntimeNetworkBridge.DomainNetworkState> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var existing = Volatile.Read(ref _networkState);
        if (existing != null)
            return existing;
        lock (_domainSync)
        {
            _networkState ??= factory();
            return _networkState;
        }
    }

    private static string NormalizeRoot(string root, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root, parameterName);
        var full = Path.GetFullPath(root);
        return full.Length > 1 &&
               (full.EndsWith(Path.DirectorySeparatorChar) ||
                full.EndsWith(Path.AltDirectorySeparatorChar))
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    internal T GetOrCreateStaticSlot<T>(int slotId, Func<T> factory)
        => GetOrCreateStaticSlot(slotId, typeof(LegacyStaticSlotOwner), factory);

    internal T GetStaticSlotForOwner<T, TOwner>(int slotId)
        => GetStaticSlot<T>(slotId, typeof(TOwner));

    internal void SetStaticSlotForOwner<T, TOwner>(int slotId, T value)
        => SetStaticSlot(slotId, typeof(TOwner), value);

    internal ref T GetStaticSlotReferenceForOwner<T, TOwner>(int slotId)
        => ref GetStaticSlotReference<T>(slotId, typeof(TOwner));

    private T GetOrCreateStaticSlot<T>(int slotId, Type ownerType, Func<T> factory)
    {
        ValidateSlotId(slotId);
        var cell = GetOrCreateStaticSlotCell<T>(slotId, ownerType);
        lock (cell)
        {
            if (Volatile.Read(ref cell.Initialized) != 0)
                return cell.Value;

            var value = factory();
            cell.Value = value;
            Volatile.Write(ref cell.Initialized, 1);
            return value;
        }
    }

    internal bool TryGetStaticSlot<T>(int slotId, out T? value)
        => TryGetStaticSlot(slotId, typeof(LegacyStaticSlotOwner), out value);

    private bool TryGetStaticSlot<T>(int slotId, Type ownerType, out T? value)
    {
        ValidateSlotId(slotId);
        var key = CreateStaticSlotKey<T>(slotId, ownerType);
        if (!_staticSlots.TryGetValue(key, out var existing))
        {
            value = default;
            return false;
        }
        var cell = CastSlotCell<T>(key, existing);
        if (Volatile.Read(ref cell.Initialized) == 0)
        {
            value = default;
            return false;
        }
        value = cell.Value;
        return true;
    }

    internal void SetStaticSlot<T>(int slotId, T value)
        => SetStaticSlot(slotId, typeof(LegacyStaticSlotOwner), value);

    private void SetStaticSlot<T>(int slotId, Type ownerType, T value)
    {
        ValidateSlotId(slotId);
        var cell = GetOrCreateStaticSlotCell<T>(slotId, ownerType);
        cell.Value = value;
        Volatile.Write(ref cell.Initialized, 1);
    }

    internal T GetStaticSlot<T>(int slotId)
        => GetStaticSlot<T>(slotId, typeof(LegacyStaticSlotOwner));

    private T GetStaticSlot<T>(int slotId, Type ownerType)
    {
        ValidateSlotId(slotId);
        var cell = GetOrCreateStaticSlotCell<T>(slotId, ownerType);
        return cell.Value;
    }

    internal ref T GetStaticSlotReference<T>(int slotId)
        => ref GetStaticSlotReference<T>(slotId, typeof(LegacyStaticSlotOwner));

    private ref T GetStaticSlotReference<T>(int slotId, Type ownerType)
    {
        ValidateSlotId(slotId);
        var cell = GetOrCreateStaticSlotCell<T>(slotId, ownerType);
        Volatile.Write(ref cell.Initialized, 1);
        return ref cell.Value;
    }

    internal void EnsureStaticTypeInitialized(
        int typeId,
        RuntimeMethodHandle initializerHandle,
        RuntimeTypeHandle ownerTypeHandle)
    {
        ValidateSlotId(typeId);
        var currentThreadId = Environment.CurrentManagedThreadId;
        var initializerKey = new StaticInitializerKey(initializerHandle, ownerTypeHandle);
        StaticTypeInitialization state;
        lock (_domainSync)
        {
            if (!_staticTypeInitializers.TryGetValue(initializerKey, out state!))
            {
                state = new StaticTypeInitialization(typeId);
                _staticTypeInitializers.Add(initializerKey, state);
            }
            else if (state.TypeId != typeId)
            {
                throw new InvalidOperationException(
                    $"MOD data domain initializer descriptor changed for method handle: " +
                    $"{state.TypeId} -> {typeId}.");
            }

            while (state.Status == StaticTypeInitializationStatus.Running &&
                   state.OwnerThreadId != currentThreadId)
            {
                Monitor.Wait(_domainSync);
            }

            if (state.Status == StaticTypeInitializationStatus.Completed)
                return;
            if (state.Status == StaticTypeInitializationStatus.Failed)
            {
                state.Failure!.Throw();
                throw new InvalidOperationException("Unreachable static initializer failure path.");
            }
            if (state.Status == StaticTypeInitializationStatus.Running)
                return;

            state.Status = StaticTypeInitializationStatus.Running;
            state.OwnerThreadId = currentThreadId;
        }

        ExceptionDispatchInfo? failure = null;
        try
        {
            var initializer = ownerTypeHandle.Equals(default(RuntimeTypeHandle))
                ? MethodBase.GetMethodFromHandle(initializerHandle)
                : MethodBase.GetMethodFromHandle(initializerHandle, ownerTypeHandle);
            var initializerMethod = initializer as MethodInfo
                              ?? throw new InvalidOperationException(
                                  $"MOD data domain initializer {typeId} is not a method.");
            if (!initializerMethod.IsStatic || initializerMethod.GetParameters().Length != 0 ||
                initializerMethod.ReturnType != typeof(void))
            {
                throw new InvalidOperationException(
                    $"MOD data domain initializer has an invalid signature: {initializerMethod}.");
            }
            initializerMethod.Invoke(null, null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            failure = ExceptionDispatchInfo.Capture(exception.InnerException);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        lock (_domainSync)
        {
            state.OwnerThreadId = 0;
            state.Failure = failure;
            state.Status = failure == null
                ? StaticTypeInitializationStatus.Completed
                : StaticTypeInitializationStatus.Failed;
            Monitor.PulseAll(_domainSync);
        }

        if (failure != null)
        {
            failure.Throw();
            throw new InvalidOperationException("Unreachable static initializer failure path.");
        }
    }

    private StaticSlotCell<T> GetOrCreateStaticSlotCell<T>(int slotId, Type ownerType)
    {
        var key = CreateStaticSlotKey<T>(slotId, ownerType);
        var cell = _staticSlots.GetOrAdd(
            key,
            static _ => new StaticSlotCell<T>());
        return CastSlotCell<T>(key, cell);
    }

    private static StaticSlotCell<T> CastSlotCell<T>(StaticSlotKey key, IStaticSlotCell cell)
    {
        if (cell is StaticSlotCell<T> typed)
            return typed;
        throw new InvalidOperationException(
            $"MOD data domain static slot {key.SlotId} owner {key.OwnerType.FullName} " +
            $"contains {cell.ValueType.FullName}, requested {typeof(T).FullName}.");
    }

    private static StaticSlotKey CreateStaticSlotKey<T>(int slotId, Type ownerType)
    {
        ArgumentNullException.ThrowIfNull(ownerType);
        // The slot identity is the rewritten field identity plus its closed owner. Keep the
        // value type out of the dictionary key so a reused slot with a different type still
        // trips the existing type-consistency guard in CastSlotCell.
        return new StaticSlotKey(slotId, ownerType);
    }

    private static void ValidateSlotId(int slotId)
    {
        if (slotId < 0)
            throw new ArgumentOutOfRangeException(nameof(slotId));
    }

    private interface IStaticSlotCell
    {
        Type ValueType { get; }
    }

    private sealed class StaticSlotCell<T> : IStaticSlotCell
    {
        internal T Value = default!;
        internal int Initialized;
        public Type ValueType => typeof(T);
    }

    private sealed class LegacyStaticSlotOwner
    {
    }

    private readonly record struct StaticSlotKey(
        int SlotId,
        Type OwnerType);

    private readonly record struct StaticInitializerKey(
        RuntimeMethodHandle MethodHandle,
        RuntimeTypeHandle OwnerTypeHandle);

    private enum StaticTypeInitializationStatus
    {
        Pending,
        Running,
        Completed,
        Failed
    }

    private sealed class StaticTypeInitialization(int typeId)
    {
        internal int TypeId { get; } = typeId;
        internal StaticTypeInitializationStatus Status;
        internal int OwnerThreadId;
        internal ExceptionDispatchInfo? Failure;
    }
}

internal static class ModDataDomainRegistry
{
    private const int Capacity = 4096;
    private static readonly object Sync = new();
    private static readonly Slot[] Slots = Enumerable.Range(0, Capacity)
        .Select(_ => new Slot())
        .ToArray();
    private static readonly ulong ProcessCookie = CreateProcessCookie();

    internal static ModDataDomain Open(ModRuntimeSession session, ModRuntimeKey key)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!key.IsValid)
            throw new ArgumentException("MOD runtime key is invalid.", nameof(key));

        var loaderKind = ToLoaderKind(key.LoaderKind);
        lock (Sync)
        {
            foreach (var slot in Slots)
            {
                var current = Volatile.Read(ref slot.Domain);
                if (current != null &&
                    ReferenceEquals(current.Session, session) &&
                    current.Key.Matches(key))
                {
                    return current;
                }
            }

            for (var index = 0; index < Slots.Length; index++)
            {
                var slot = Slots[index];
                if (Volatile.Read(ref slot.Domain) != null)
                    continue;

                var generation = NextGeneration(slot.Generation);
                var token = new ModDataDomainToken(
                    ProcessCookie,
                    generation,
                    index,
                    loaderKind);
                var domain = new ModDataDomain(key, token, session);
                slot.Generation = generation;
                Volatile.Write(ref slot.Domain, domain);
                return domain;
            }
        }

        throw new InvalidOperationException(
            $"MOD data domain capacity exhausted ({Capacity}).");
    }

    internal static bool TryResolve(
        ModDataDomainToken token,
        out ModDataDomain domain)
    {
        domain = null!;
        if (!token.IsValid ||
            token.ProcessCookie != ProcessCookie ||
            (uint)token.SlotIndex >= (uint)Slots.Length)
        {
            return false;
        }

        var current = Volatile.Read(ref Slots[token.SlotIndex].Domain);
        if (current == null ||
            current.Token != token ||
            current.Token.LoaderKind != token.LoaderKind)
        {
            return false;
        }

        domain = current;
        return true;
    }

    internal static bool Close(ModDataDomainToken token)
    {
        if (!token.IsValid ||
            token.ProcessCookie != ProcessCookie ||
            (uint)token.SlotIndex >= (uint)Slots.Length)
        {
            return false;
        }

        lock (Sync)
        {
            var slot = Slots[token.SlotIndex];
            var current = Volatile.Read(ref slot.Domain);
            if (current == null || current.Token != token)
                return false;
            Volatile.Write(ref slot.Domain, null);
            return true;
        }
    }

    internal static bool TryGetKey(
        ModDataDomainToken token,
        out ModRuntimeKey key)
    {
        if (TryResolve(token, out var domain))
        {
            key = domain.Key;
            return true;
        }
        key = default;
        return false;
    }

    private static long NextGeneration(long current) =>
        current == long.MaxValue ? 1 : current + 1;

    private static ModDataDomainLoaderKind ToLoaderKind(string loaderKind) =>
        loaderKind switch
        {
            "StArray.Android.Native" => ModDataDomainLoaderKind.AndroidManaged,
            "xphorror.PcModCompat" => ModDataDomainLoaderKind.PcCompat,
            "StArray.Host" => ModDataDomainLoaderKind.Host,
            _ => ModDataDomainLoaderKind.Other
        };

    /// <summary>
    /// Finds a live domain other than <paramref name="excluding"/> that owns
    /// <paramref name="fullPath"/>. Used to reject cross-MOD filesystem access with a
    /// diagnosable owner instead of a generic denial.
    /// </summary>
    /// <remarks>
    /// Cold path only: filesystem denials are not a hot path, so a bounded scan over the
    /// fixed slot array is acceptable. Never call this from a per-frame path.
    /// </remarks>
    internal static bool TryFindForeignPathOwner(
        string fullPath,
        ModDataDomainToken excluding,
        out ModRuntimeKey owner)
    {
        owner = default;
        if (string.IsNullOrEmpty(fullPath))
            return false;

        foreach (var slot in Slots)
        {
            ModDataDomain? domain;
            lock (Sync)
                domain = slot.Domain;
            if (domain == null || domain.Token.Equals(excluding))
                continue;
            if (!domain.TryGetPathRoots(out var roots))
                continue;
            foreach (var root in roots.OwnedRoots)
            {
                if (!ModDataDomainPaths.IsWithin(root, fullPath))
                    continue;
                owner = domain.Key;
                return true;
            }
        }
        return false;
    }

    private static ulong CreateProcessCookie()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt64(bytes);
        } while (value == 0);
        return value;
    }

    private sealed class Slot
    {
        internal long Generation;
        internal ModDataDomain? Domain;
    }
}

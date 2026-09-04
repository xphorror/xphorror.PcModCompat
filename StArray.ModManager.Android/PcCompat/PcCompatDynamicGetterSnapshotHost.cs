using System.Collections.Concurrent;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatDynamicGetterSnapshotHost
{
    private const string LogTag = "PcCompatDynamicGetterSnapshot";
    private static readonly ConcurrentDictionary<ResolutionLogKey, long> LoggedUnavailable = new();
    private static SnapshotPublication? s_publication;

    [ThreadStatic]
    private static SnapshotPublication? t_publication;
    [ThreadStatic]
    private static long t_resourceGeneration;
    [ThreadStatic]
    private static PcCompatGameSnapshot? t_snapshot;

    private readonly record struct ResolutionLogKey(
        string ModId,
        string DeclaringType,
        string MemberName);

    private readonly record struct PublishedOwnerKey(string ModId, long ResourceGeneration);

    private sealed class SnapshotPublication(PcCompatOverlaySnapshot overlay)
    {
        public PcCompatOverlaySnapshot Overlay { get; } = overlay;
        public ConcurrentDictionary<long, PcCompatGameSnapshot> Snapshots { get; } = new();
        public ConcurrentDictionary<PublishedOwnerKey, byte> PublishedOwners { get; } = new();
    }

    private enum Scalar
    {
        IsScnGame,
        IsPaused,
        CurrentSeqId,
        IsNoFail,
        Progress,
        CheckpointsUsed,
        IsGameWorld,
        ConductorAddOffset,
        ConductorSongPositionMinusi,
        SongPitch,
        IsAuto,
        PercentAcc,
        PercentXAcc,
        PlanetSpeed
    }

    public static PcCompatSnapshotScalarResolution TryResolve(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        Type requestedType,
        object? instance,
        out object? value)
    {
        var publication = GetPublication();
        var overlay = publication.Overlay;
        var snapshot = GetSnapshot(publication, owner.ResourceSessionGeneration);
        if (TryResolveObjectRoot(
                declaringType,
                memberName,
                snapshot,
                out var pointer,
                out var proxyAssembly,
                out var proxyTypeName))
        {
            if (!overlay.ProviderAvailable || overlay.Generation == 0)
            {
                value = null;
                return PcCompatSnapshotScalarResolution.Unavailable;
            }
            if (pointer == 0)
            {
                if (!overlay.IsGameReady || !overlay.IsGameWorld)
                {
                    value = null;
                    return PcCompatSnapshotScalarResolution.Resolved;
                }
                LogUnavailable(
                    owner,
                    declaringType,
                    memberName,
                    PcCompatGameSnapshotFields.State,
                    overlay,
                    snapshot.ValidFields);
                value = null;
                return PcCompatSnapshotScalarResolution.Unavailable;
            }

            if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                    proxyAssembly,
                    proxyTypeName,
                    out var expectedProxyType))
            {
                value = null;
                return PcCompatSnapshotScalarResolution.Unavailable;
            }
            var proxyType = requestedType == typeof(object)
                ? expectedProxyType
                : requestedType;
            if (requestedType != typeof(object) &&
                requestedType != expectedProxyType &&
                !requestedType.IsAssignableFrom(expectedProxyType))
            {
                value = null;
                return PcCompatSnapshotScalarResolution.Unavailable;
            }
            if (!PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType(proxyType))
            {
                value = null;
                return PcCompatSnapshotScalarResolution.Unavailable;
            }
            if (!PcCompatUnityMainExecutionContext.IsActive)
            {
                // Object wrappers and their GC handles are UnityMain-owned. Returning unavailable
                // here makes the dynamic bridge use its existing bounded UnityMain object-graph
                // scheduler; immutable scalar snapshot reads remain worker-safe.
                value = null;
                return PcCompatSnapshotScalarResolution.Unavailable;
            }

            // Publish the epoch before the dynamic bridge canonicalizes the wrapper. Its cache key
            // includes this epoch, so a scene transition cannot reuse an old pointer binding.
            PublishReversePatchOnce(publication, owner, snapshot);
            value = PcCompatManagedComponentOwnerHost.WrapNativeProxyPointer(
                proxyType,
                (nint)pointer);
            return PcCompatSnapshotScalarResolution.Resolved;
        }

        _ = instance;
        if (!TryResolveScalar(declaringType, memberName, out var scalar, out var field))
        {
            value = null;
            return PcCompatSnapshotScalarResolution.Unhandled;
        }
        if (!snapshot.Has(field, owner.ResourceSessionGeneration))
        {
            LogUnavailable(owner, declaringType, memberName, field, overlay, snapshot.ValidFields);

            // A known gameplay scalar must not fall back into a half-constructed IL2CPP object
            // graph while outside gameplay or during a scene transition. The current generation's
            // zero/false/default state is authoritative there; live values replace it after the
            // UnityMain sampler publishes the first valid field group.
            if (overlay.ProviderAvailable &&
                overlay.Generation != 0 &&
                (!overlay.IsGameReady || !overlay.IsGameWorld))
            {
                value = Read(snapshot, scalar);
                return TryConvert(requestedType, ref value)
                    ? PcCompatSnapshotScalarResolution.Resolved
                    : PcCompatSnapshotScalarResolution.Unavailable;
            }

            value = null;
            return PcCompatSnapshotScalarResolution.Unavailable;
        }

        PublishReversePatchOnce(publication, owner, snapshot);

        value = Read(snapshot, scalar);
        return TryConvert(requestedType, ref value)
            ? PcCompatSnapshotScalarResolution.Resolved
            : PcCompatSnapshotScalarResolution.Unavailable;
    }

    internal static void RefreshOnUnityMain()
        => PublishOverlaySnapshot(PcCompatNativeHookRules.GetSharedGameSnapshot());

    internal static void PublishOverlaySnapshot(PcCompatOverlaySnapshot overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        while (true)
        {
            var current = Volatile.Read(ref s_publication);
            if (current != null && ReferenceEquals(current.Overlay, overlay))
                return;
            var replacement = new SnapshotPublication(overlay);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref s_publication, replacement, current),
                    current))
                return;
        }
    }

    internal static PcCompatGameSnapshot GetPublishedSnapshotForTests(long resourceGeneration)
        => GetSnapshot(GetPublication(), resourceGeneration);

    internal static void ClearPublishedSnapshotForTests()
    {
        Volatile.Write(ref s_publication, null);
        t_publication = null;
        t_resourceGeneration = 0;
        t_snapshot = null;
    }

    private static SnapshotPublication GetPublication()
    {
        var publication = Volatile.Read(ref s_publication);
        if (publication != null)
            return publication;
        PublishOverlaySnapshot(PcCompatNativeHookRules.GetSharedGameSnapshot());
        return Volatile.Read(ref s_publication)
               ?? throw new InvalidOperationException("Shared game snapshot publication failed.");
    }

    private static PcCompatGameSnapshot GetSnapshot(
        SnapshotPublication publication,
        long resourceGeneration)
    {
        if (ReferenceEquals(t_publication, publication) &&
            t_resourceGeneration == resourceGeneration &&
            t_snapshot != null)
        {
            return t_snapshot;
        }

        var snapshot = publication.Snapshots.GetOrAdd(
            resourceGeneration,
            generation => PcCompatGameSnapshot.FromOverlay(publication.Overlay, generation));
        t_publication = publication;
        t_resourceGeneration = resourceGeneration;
        t_snapshot = snapshot;
        return snapshot;
    }

    private static void PublishReversePatchOnce(
        SnapshotPublication publication,
        PcCompatManagedExecutionState owner,
        PcCompatGameSnapshot snapshot)
    {
        var key = new PublishedOwnerKey(owner.ModId, owner.ResourceSessionGeneration);
        if (publication.PublishedOwners.TryAdd(key, 0))
            PcCompatReversePatchBridge.PublishSnapshot(snapshot);
    }

    private static bool TryConvert(Type requestedType, ref object? value)
    {
        if (value is null || requestedType == typeof(object) || requestedType.IsInstanceOfType(value))
            return true;

        try
        {
            value = Convert.ChangeType(
                value,
                Nullable.GetUnderlyingType(requestedType) ?? requestedType,
                System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static void LogUnavailable(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        PcCompatGameSnapshotFields requiredField,
        PcCompatOverlaySnapshot overlay,
        PcCompatGameSnapshotFields validFields)
    {
        var key = new ResolutionLogKey(
            owner.ModId,
            declaringType.FullName ?? declaringType.Name,
            memberName);
        while (true)
        {
            if (!LoggedUnavailable.TryGetValue(key, out var previousGeneration))
            {
                if (LoggedUnavailable.TryAdd(key, owner.ResourceSessionGeneration))
                    break;
                continue;
            }
            if (previousGeneration == owner.ResourceSessionGeneration)
                return;
            if (LoggedUnavailable.TryUpdate(
                    key,
                    owner.ResourceSessionGeneration,
                    previousGeneration))
                break;
        }

        Logger.Info(
            LogTag,
            $"unavailable mod={owner.ModId} resourceGeneration={owner.ResourceSessionGeneration} " +
            $"member={key.DeclaringType}.{memberName} required={requiredField} valid={validFields} " +
            $"provider={overlay.ProviderAvailable} snapshotGeneration={overlay.Generation} " +
            $"timeline={overlay.TimelineSnapshotCount} accuracy={overlay.AccuracySnapshotCount} " +
            $"bpm={overlay.BpmSnapshotCount} gameReady={overlay.IsGameReady} " +
            $"gameWorld={overlay.IsGameWorld} scnGame={overlay.IsScnGame}");
    }

    private static bool TryResolveScalar(
        Type type,
        string name,
        out Scalar scalar,
        out PcCompatGameSnapshotFields field)
    {
        var typeName = type.Name;
        var resolved = (typeName, name) switch
        {
            ("ADOBase", "isScnGame") => (Scalar.IsScnGame, PcCompatGameSnapshotFields.State),
            ("scrController", "paused") => (Scalar.IsPaused, PcCompatGameSnapshotFields.State),
            ("scrController", "currentSeqID") => (Scalar.CurrentSeqId, PcCompatGameSnapshotFields.CurrentSeqId),
            ("scrController", "noFail") => (Scalar.IsNoFail, PcCompatGameSnapshotFields.State),
            ("scrController", "percentComplete") => (Scalar.Progress, PcCompatGameSnapshotFields.Progress),
            ("scrController", "checkpointsUsed") => (Scalar.CheckpointsUsed, PcCompatGameSnapshotFields.Checkpoints),
            ("scrController", "speed") => (Scalar.PlanetSpeed, PcCompatGameSnapshotFields.PlanetSpeed),
            ("scrConductor", "isGameWorld") => (Scalar.IsGameWorld, PcCompatGameSnapshotFields.State),
            ("scrConductor", "addoffset") => (Scalar.ConductorAddOffset, PcCompatGameSnapshotFields.Conductor),
            ("scrConductor", "songposition_minusi") => (Scalar.ConductorSongPositionMinusi, PcCompatGameSnapshotFields.Conductor),
            ("AudioSource", "pitch") => (Scalar.SongPitch, PcCompatGameSnapshotFields.SongPitch),
            ("RDC", "auto") => (Scalar.IsAuto, PcCompatGameSnapshotFields.State),
            ("scrMistakesManager", "percentAcc") => (Scalar.PercentAcc, PcCompatGameSnapshotFields.Accuracy),
            ("scrMistakesManager", "percentXAcc") => (Scalar.PercentXAcc, PcCompatGameSnapshotFields.Accuracy),
            ("PlanetarySystem", "speed") => (Scalar.PlanetSpeed, PcCompatGameSnapshotFields.PlanetSpeed),
            _ => ((Scalar)(-1), PcCompatGameSnapshotFields.None)
        };
        scalar = resolved.Item1;
        field = resolved.Item2;
        return field != PcCompatGameSnapshotFields.None;
    }

    internal static bool TryResolveObjectRoot(
        Type type,
        string name,
        PcCompatGameSnapshot snapshot,
        out long pointer,
        out string proxyAssembly,
        out string proxyTypeName)
    {
        var resolved = (type.Name, name) switch
        {
            ("ADOBase", "controller") or
                ("scrController", "instance") or
                ("scrController", "_instance") =>
                (snapshot.ControllerPointer, "Assembly-CSharp", "scrController"),
            ("ADOBase", "conductor") or
                ("scrConductor", "instance") =>
                (snapshot.ConductorPointer, "Assembly-CSharp", "scrConductor"),
            ("ADOBase", "lm") or
                ("scrLevelMaker", "instance") =>
                (snapshot.LevelMakerPointer, "Assembly-CSharp", "scrLevelMaker"),
            ("scrController", "currFloor") =>
                (snapshot.CurrentFloorPointer, "Assembly-CSharp", "scrFloor"),
            ("scrController", "firstFloor") =>
                (snapshot.FirstFloorPointer, "Assembly-CSharp", "scrFloor"),
            ("scrConductor", "song") =>
                (snapshot.SongPointer, "UnityEngine.AudioModule", "UnityEngine.AudioSource"),
            ("scrController", "planetarySystem") =>
                (snapshot.PlanetarySystemPointer, "Assembly-CSharp", "PlanetarySystem"),
            _ => (0L, string.Empty, string.Empty)
        };
        pointer = resolved.Item1;
        proxyAssembly = resolved.Item2;
        proxyTypeName = resolved.Item3;
        return proxyTypeName.Length != 0;
    }

    private static object Read(PcCompatGameSnapshot snapshot, Scalar scalar)
        => scalar switch
        {
            Scalar.IsScnGame => snapshot.IsScnGame,
            Scalar.IsPaused => snapshot.IsPaused,
            Scalar.CurrentSeqId => snapshot.CurrentSeqId,
            Scalar.IsNoFail => snapshot.IsNoFail,
            Scalar.Progress => snapshot.Progress,
            Scalar.CheckpointsUsed => snapshot.CheckpointsUsed,
            Scalar.IsGameWorld => snapshot.IsGameWorld,
            Scalar.ConductorAddOffset => snapshot.ConductorAddOffset,
            Scalar.ConductorSongPositionMinusi => snapshot.ConductorSongPositionMinusi,
            Scalar.SongPitch => snapshot.SongPitch,
            Scalar.IsAuto => snapshot.IsAuto,
            Scalar.PercentAcc => snapshot.PercentAcc,
            Scalar.PercentXAcc => snapshot.PercentXAcc,
            Scalar.PlanetSpeed => snapshot.PlanetSpeed,
            _ => throw new ArgumentOutOfRangeException(nameof(scalar))
        };
}

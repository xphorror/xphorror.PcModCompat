using System.Collections.Concurrent;
using ImGuiNET;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Per-MOD scope for the shared Host ImGui context: injects a stable ID namespace, keeps one
/// MOD's UI fault from taking down the Host's frame, and quarantines a MOD whose draw callback
/// keeps failing.
/// </summary>
/// <remarks>
/// <para>
/// Every MOD draws into one context and one final render pass (MOD_RUNTIME_ISOLATION: "稳态只
/// 增加 owner scope 和栈深度快照，不为每个 MOD建立独立 ImGui context"). Without an ID
/// namespace two MODs that both open a window called "Settings" collide on window state,
/// popups and drag/drop; without a fault boundary a MOD that throws mid-draw leaves the Host's
/// own <c>Begin</c>/<c>End</c> unpaired and corrupts the whole frame.
/// </para>
/// <para>
/// Deliberate limitation: ImGui.NET exposes no context introspection, so the scope cannot read
/// ImGui's real window/style/color stack depths and therefore cannot unwind pushes the MOD
/// leaked. It guarantees the Host's own pairing and the ID namespace; full stack-depth
/// snapshot/restore needs a native cimgui export and is tracked as the remaining part of this
/// clause.
/// </para>
/// </remarks>
public static class UiOwnerScope
{
    /// <summary>Consecutive draw faults before a MOD's UI is quarantined for this generation.</summary>
    private const int QuarantineThreshold = 4;

    private static readonly ConcurrentDictionary<OwnerKey, int> ConsecutiveFaults = new();
    private static readonly ConcurrentDictionary<OwnerKey, byte> Quarantined = new();

    private readonly record struct OwnerKey(string OwnerId, long Generation);

    /// <summary>
    /// Whether this owner's UI is currently quarantined and must be skipped. A reload lands on
    /// a new generation, so quarantine never outlives the session that caused it.
    /// </summary>
    public static bool IsQuarantined(string ownerId, long generation) =>
        !string.IsNullOrEmpty(ownerId) &&
        Quarantined.ContainsKey(new OwnerKey(ownerId, generation));

    /// <summary>
    /// Runs <paramref name="draw"/> inside this owner's ID namespace with a fault boundary.
    /// Returns false when the owner is quarantined or the draw faulted; the Host's own ImGui
    /// pairing is always restored before returning.
    /// </summary>
    public static bool TryDraw(
        string ownerId,
        long generation,
        string description,
        Action draw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(draw);
        var key = new OwnerKey(ownerId, generation);
        if (Quarantined.ContainsKey(key))
            return false;

        // ID injection requires a live ImGui context. Callers are Host draw loops, but a
        // context can legitimately be absent (headless host tests, a draw request racing
        // renderer teardown) and cimgui would fault the process rather than return an error.
        var hasContext = HasImGuiContext();
        if (hasContext)
        {
            // Stable per-owner namespace: identical control/window names in two MODs no longer
            // resolve to the same ImGui ID.
            ImGui.PushID(ownerId);
        }
        try
        {
            draw();
            ConsecutiveFaults.TryRemove(key, out _);
            return true;
        }
        catch (Exception exception)
        {
            var faults = ConsecutiveFaults.AddOrUpdate(key, 1, static (_, current) => current + 1);
            if (faults >= QuarantineThreshold && Quarantined.TryAdd(key, 0))
            {
                Logger.Error(
                    nameof(UiOwnerScope),
                    $"{description} for owner={ownerId} generation={generation} faulted " +
                    $"{faults} times in a row; its UI is quarantined for this session.");
            }
            else
            {
                Logger.Error(
                    nameof(UiOwnerScope),
                    $"{description} for owner={ownerId} generation={generation} faulted: {exception}");
            }
            return false;
        }
        finally
        {
            // Runs on both paths: the ID namespace must never leak into the Host's own UI.
            if (hasContext)
                ImGui.PopID();
        }
    }

    /// <summary>
    /// Whether a live ImGui context exists. cimgui dereferences the context pointer without a
    /// null check, so every ImGui call here must be gated on this instead of relying on an
    /// exception.
    /// </summary>
    private static bool HasImGuiContext()
    {
        try
        {
            return ImGui.GetCurrentContext() != IntPtr.Zero;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException)
        {
            // No ImGui native library at all (host tests, tooling): treat as no context.
            return false;
        }
    }

    /// <summary>Clears fault/quarantine bookkeeping for one owner generation.</summary>
    public static void Release(string ownerId, long generation)
    {
        if (string.IsNullOrEmpty(ownerId))
            return;
        var key = new OwnerKey(ownerId, generation);
        ConsecutiveFaults.TryRemove(key, out _);
        Quarantined.TryRemove(key, out _);
    }

    internal static void ClearForTests()
    {
        ConsecutiveFaults.Clear();
        Quarantined.Clear();
    }
}

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Xphorror.PcModCompat;

/// <summary>
/// Owner-scoped bridge for Rewired's integer action polling surface. Touch mode
/// bypasses the desktop player object; External preserves it; Hybrid merges both.
/// </summary>
public static class PcCompatRewiredInputBridge
{
    private static readonly ConcurrentDictionary<(Type Type, string Method),
        Func<object, int, bool>?> OriginalQueries = new();

    public static bool GetButtonOwned(
        object player,
        int actionId,
        int callsiteToken,
        string modId)
        => Query(player, actionId, callsiteToken, modId, "GetButton", EdgeKind.Held);

    public static bool GetButtonDownOwned(
        object player,
        int actionId,
        int callsiteToken,
        string modId)
        => Query(player, actionId, callsiteToken, modId, "GetButtonDown", EdgeKind.Down);

    public static bool GetButtonUpOwned(
        object player,
        int actionId,
        int callsiteToken,
        string modId)
        => Query(player, actionId, callsiteToken, modId, "GetButtonUp", EdgeKind.Up);

    private static bool Query(
        object player,
        int actionId,
        int callsiteToken,
        string modId,
        string originalMethod,
        EdgeKind edge)
    {
        if (!PcCompatKeyViewerConsumerRuntime.TryGetActionState(modId, actionId, out var state))
            return InvokeOriginal(player, originalMethod, actionId);

        var consumer = edge switch
        {
            EdgeKind.Held => ReadHeld(modId, actionId, callsiteToken, state),
            EdgeKind.Down => ReadEdge(modId, actionId, callsiteToken, state, down: true),
            EdgeKind.Up => ReadEdge(modId, actionId, callsiteToken, state, down: false),
            _ => false
        };
        if (state.Mode == PcCompatKeyViewerInputMode.Touch)
            return consumer;
        return InvokeOriginal(player, originalMethod, actionId) || consumer;
    }

    private static bool ReadHeld(
        string modId,
        int actionId,
        int callsiteToken,
        PcCompatKeyViewerConsumerKeyState state)
        => CursorState.Current.ReadHeld(modId, actionId, callsiteToken, state);

    private static bool ReadEdge(
        string modId,
        int actionId,
        int callsiteToken,
        PcCompatKeyViewerConsumerKeyState state,
        bool down)
        => CursorState.Current.ReadEdge(
            modId,
            state.RegistrationGeneration,
            actionId,
            callsiteToken,
            down,
            down ? state.DownOrdinal : state.UpOrdinal);

    private static bool InvokeOriginal(object player, string methodName, int actionId)
    {
        if (player == null)
            return false;
        var query = OriginalQueries.GetOrAdd(
            (player.GetType(), methodName),
            static key => CompileOriginal(key.Type, key.Method));
        return query?.Invoke(player, actionId) == true;
    }

    private static Func<object, int, bool>? CompileOriginal(Type type, string methodName)
    {
        var method = type.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [typeof(int)],
            modifiers: null);
        if (method == null || method.ReturnType != typeof(bool))
            return null;
        var instance = Expression.Parameter(typeof(object), "instance");
        var action = Expression.Parameter(typeof(int), "action");
        return Expression.Lambda<Func<object, int, bool>>(
            Expression.Call(Expression.Convert(instance, type), method, action),
            instance,
            action).Compile();
    }

    private enum EdgeKind
    {
        Held,
        Down,
        Up
    }

    private sealed class CursorState
    {
        [ThreadStatic]
        private static CursorState? t_current;
        private readonly Dictionary<CursorKey, ulong> _edges = [];
        private readonly Dictionary<CursorKey, HeldCursor> _held = [];

        public static CursorState Current => t_current ??= new CursorState();

        public bool ReadEdge(
            string modId,
            long generation,
            int actionId,
            int callsiteToken,
            bool down,
            ulong ordinal)
        {
            var key = new CursorKey(modId, generation, actionId, callsiteToken, down);
            if (!_edges.TryGetValue(key, out var previous))
            {
                _edges[key] = ordinal;
                return ordinal != 0;
            }
            _edges[key] = ordinal;
            return ordinal > previous;
        }

        public bool ReadHeld(
            string modId,
            int actionId,
            int callsiteToken,
            PcCompatKeyViewerConsumerKeyState state)
        {
            var key = new CursorKey(
                modId,
                state.RegistrationGeneration,
                actionId,
                callsiteToken,
                false);
            if (!_held.TryGetValue(key, out var cursor))
            {
                cursor = new HeldCursor(state.DownOrdinal, state.UpOrdinal, false);
            }
            if (cursor.DownOrdinal < state.DownOrdinal)
            {
                cursor = cursor with
                {
                    DownOrdinal = cursor.DownOrdinal + 1,
                    Held = true
                };
            }
            else if (cursor.UpOrdinal < state.UpOrdinal)
            {
                cursor = cursor with
                {
                    UpOrdinal = cursor.UpOrdinal + 1,
                    Held = false
                };
            }
            else
            {
                cursor = cursor with { Held = state.Held };
            }
            _held[key] = cursor;
            return cursor.Held;
        }

        private readonly record struct CursorKey(
            string ModId,
            long Generation,
            int ActionId,
            int CallsiteToken,
            bool Down);

        private readonly record struct HeldCursor(
            ulong DownOrdinal,
            ulong UpOrdinal,
            bool Held);
    }
}

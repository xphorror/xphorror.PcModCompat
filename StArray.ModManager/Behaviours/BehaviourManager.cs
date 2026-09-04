using ImGuiNET;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Behaviours;

/// <summary>
/// 管理所有 <see cref="GameBehaviour"/> 的生命周期。
/// 静态方法，无需实例化。
///
/// 用户回调一律在锁外执行 —— 它们可能反过来调用 <see cref="Add"/> / <see cref="Remove"/>。
/// 异常按 Unity 的做法处理：隔离到单个行为、记录、继续，不自动禁用。
/// </summary>
public static class BehaviourManager
{
    private static readonly List<GameBehaviour> _behaviours = new();
    private static readonly List<GameBehaviour> _pendingAdd = new();
    private static readonly List<GameBehaviour> _pendingRemove = new();
    private static readonly HashSet<GameBehaviour> _suspended = new();
    private static readonly object _lock = new();

    /// <summary>当前活跃的行为列表（只读快照）</summary>
    public static IReadOnlyList<GameBehaviour> Behaviours
    {
        get { lock (_lock) return _behaviours.ToList(); }
    }

    /// <summary>活跃行为数量</summary>
    public static int Count { get { lock (_lock) return _behaviours.Count; } }

    internal static bool RequiresFrame
    {
        get
        {
            lock (_lock)
                return _behaviours.Count > 0 || _pendingAdd.Count > 0 || _pendingRemove.Count > 0;
        }
    }

    /// <summary>
    /// 添加一个行为实例。下一帧调用 OnAwake / OnEnable，随后是 OnStart。
    /// </summary>
    public static T Add<T>(T behaviour) where T : GameBehaviour
    {
        behaviour.OwnerId = HookHelper.CurrentOwnerId;
        behaviour.RuntimeSession = HookHelper.CurrentRuntimeSession;
        behaviour.RuntimeKey = HookHelper.CurrentRuntimeKey;
        if (behaviour.RuntimeSession != null &&
            !behaviour.RuntimeSession.CanRegisterOwnedResource(behaviour.RuntimeKey))
        {
            behaviour.IsDestroyed = true;
            return behaviour;
        }
        if (behaviour.RuntimeSession != null &&
            !ModOwnedResourceRegistry.TryRegister(
                behaviour.RuntimeKey,
                ModOwnedResourceKind.Behaviour,
                $"instance=0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(behaviour):X}"))
        {
            behaviour.IsDestroyed = true;
            return behaviour;
        }
        lock (_lock)
        {
            _pendingAdd.Add(behaviour);
        }
        return behaviour;
    }

    /// <summary>
    /// 移除并销毁一个行为实例。下一帧调用 OnDisable / OnStop。
    /// </summary>
    public static void Remove(GameBehaviour behaviour)
    {
        lock (_lock)
        {
            _pendingRemove.Add(behaviour);
        }
    }

    /// <summary>
    /// 移除并销毁指定类型的所有行为。
    /// </summary>
    public static void RemoveAll<T>() where T : GameBehaviour
    {
        lock (_lock)
        {
            foreach (var b in _behaviours)
                if (b is T)
                    _pendingRemove.Add(b);
        }
    }

    /// <summary>
    /// 移除并销毁所有行为。
    /// </summary>
    public static void RemoveAll()
    {
        lock (_lock)
        {
            _pendingRemove.AddRange(_behaviours);
        }
    }

    /// <summary>
    /// Synchronously retires all behaviours created by one MOD. This prevents
    /// a collectible native ALC from being kept alive by the frame scheduler.
    /// </summary>
    internal static void RetireOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        GameBehaviour[] active;
        lock (_lock)
        {
            active = _behaviours
                .Concat(_pendingAdd)
                .Where(behaviour => string.Equals(
                    behaviour.OwnerId,
                    ownerId,
                    StringComparison.Ordinal))
                .Distinct()
                .ToArray();
            foreach (var behaviour in active)
                _behaviours.Remove(behaviour);
            foreach (var behaviour in active)
                _suspended.Remove(behaviour);

            _pendingAdd.RemoveAll(behaviour => string.Equals(
                behaviour.OwnerId,
                ownerId,
                StringComparison.Ordinal));
            _pendingRemove.RemoveAll(behaviour => string.Equals(
                behaviour.OwnerId,
                ownerId,
                StringComparison.Ordinal));
        }

        foreach (var behaviour in active)
        {
            if (behaviour.IsDestroyed)
                continue;

            behaviour.IsDestroyed = true;
            if (behaviour.Awoken && behaviour.EnabledRaw)
                SafeDisable(behaviour);
            if (!behaviour.Started)
                continue;

            try { InvokeOwned(behaviour, behaviour.OnStop, cleanup: true); }
            catch (Exception ex) { LogFault(behaviour, nameof(GameBehaviour.OnStop), ex); }
        }

        // OnDisable/OnStop may enqueue replacement behaviours. Retirement is a
        // hard owner boundary, so those callbacks cannot repopulate the scheduler.
        lock (_lock)
        {
            _pendingAdd.RemoveAll(behaviour => string.Equals(
                behaviour.OwnerId,
                ownerId,
                StringComparison.Ordinal));
            _pendingRemove.RemoveAll(behaviour => string.Equals(
                behaviour.OwnerId,
                ownerId,
                StringComparison.Ordinal));
        }
    }

    /// <summary>挂起永久 native hook MOD 的行为，保留实例以便重新启用。</summary>
    internal static void SuspendOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        GameBehaviour[] active;
        lock (_lock)
        {
            active = _behaviours
                .Concat(_pendingAdd)
                .Where(behaviour => string.Equals(
                    behaviour.OwnerId,
                    ownerId,
                    StringComparison.Ordinal) &&
                    !behaviour.IsDestroyed &&
                    behaviour.EnabledRaw)
                .Distinct()
                .ToArray();
            foreach (var behaviour in active)
                _suspended.Add(behaviour);
        }

        foreach (var behaviour in active)
            behaviour.Enabled = false;
    }

    /// <summary>恢复之前由 SuspendOwner 挂起且仍存在的行为。</summary>
    internal static void ResumeOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        GameBehaviour[] suspended;
        lock (_lock)
        {
            suspended = _suspended
                .Where(behaviour => string.Equals(
                    behaviour.OwnerId,
                    ownerId,
                    StringComparison.Ordinal) &&
                    (_behaviours.Contains(behaviour) || _pendingAdd.Contains(behaviour)) &&
                    !behaviour.IsDestroyed)
                .ToArray();
            foreach (var behaviour in suspended)
                _suspended.Remove(behaviour);
        }

        foreach (var behaviour in suspended)
            behaviour.Enabled = true;
    }

    /// <summary>
    /// 获取第一个指定类型的行为，若不存在返回 null。
    /// </summary>
    public static T? Get<T>() where T : GameBehaviour
    {
        lock (_lock)
        {
            return _behaviours.OfType<T>().FirstOrDefault();
        }
    }

    /// <summary>
    /// 获取所有指定类型的行为。
    /// </summary>
    public static List<T> GetAll<T>() where T : GameBehaviour
    {
        lock (_lock)
        {
            return _behaviours.OfType<T>().ToList();
        }
    }

    // ── 异常处理 ──

    /// <summary>
    /// 记录回调异常。与 Unity 一致：不禁用该行为，下一帧照旧调用，
    /// 因此一个坏掉的行为会持续报错 —— 这是有意的，止损由使用者决定。
    /// </summary>
    private static void LogFault(GameBehaviour b, string phase, Exception ex)
        => Logger.Error(nameof(BehaviourManager), $"{b.GetType().Name}.{phase}: {ex}");

    internal static void SafeEnable(GameBehaviour b)
    {
        try { InvokeOwned(b, b.OnEnable); }
        catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnEnable), ex); }
    }

    internal static void SafeDisable(GameBehaviour b)
    {
        try { InvokeOwned(b, b.OnDisable, cleanup: true); }
        catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnDisable), ex); }
    }

    private static bool InvokeOwned(
        GameBehaviour behaviour,
        Action callback,
        bool cleanup = false)
    {
        if (string.IsNullOrWhiteSpace(behaviour.OwnerId))
        {
            callback();
            return true;
        }

        IDisposable? callbackLease = null;
        if (behaviour.RuntimeSession != null)
        {
            var entered = cleanup
                ? behaviour.RuntimeSession.TryEnterCleanupCallback(
                    behaviour.RuntimeKey,
                    out callbackLease)
                : behaviour.RuntimeSession.TryEnterCallback(
                    behaviour.RuntimeKey,
                    out callbackLease);
            if (!entered)
                return false;
        }

        using (callbackLease)
        using (behaviour.RuntimeSession != null
                   ? HookHelper.EnterOwnerScope(
                       behaviour.OwnerId,
                       behaviour.RuntimeSession,
                       behaviour.RuntimeKey)
                   : HookHelper.EnterOwnerScope(behaviour.OwnerId))
            callback();
        return true;
    }

    private static GameBehaviour[] Snapshot()
    {
        lock (_lock) return _behaviours.ToArray();
    }

    // ── 内部驱动（由 ModManagerUI 每帧调用） ──

    /// <summary>处理增删队列，派发 OnAwake/OnEnable 与 OnDisable/OnStop，在 Update 之前调用。</summary>
    internal static void ProcessPending()
    {
        GameBehaviour[] toRemove, toAdd;

        // 队列先取出再清空，之后所有回调都在锁外跑：
        // OnStop 里调用 Remove() 是很自然的写法，若在 foreach 中就会改到正在遍历的集合。
        lock (_lock)
        {
            if (_pendingRemove.Count == 0 && _pendingAdd.Count == 0) return;

            toRemove = _pendingRemove.ToArray();
            _pendingRemove.Clear();
            toAdd = _pendingAdd.ToArray();
            _pendingAdd.Clear();

            foreach (var b in toRemove)
            {
                _behaviours.Remove(b);
                _suspended.Remove(b);
            }
            _behaviours.AddRange(toAdd);
        }

        foreach (var b in toRemove)
        {
            if (b.IsDestroyed) continue;
            b.IsDestroyed = true;
            if (b.Awoken && b.EnabledRaw) SafeDisable(b);
            if (b.Started)
            {
                try { InvokeOwned(b, b.OnStop, cleanup: true); }
                catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnStop), ex); }
            }
        }

        foreach (var b in toAdd)
        {
            if (b.IsDestroyed || b.Awoken) continue;
            b.Awoken = true; // 先置位：抛异常也不重复 Awake
            try
            {
                if (!InvokeOwned(b, b.OnAwake))
                {
                    b.IsDestroyed = true;
                    continue;
                }
            }
            catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnAwake), ex); }
            if (b.EnabledRaw) SafeEnable(b);
        }
    }

    /// <summary>按 Unity 的阶段顺序派发 OnStart → OnUpdate → OnLateUpdate。</summary>
    internal static void Update(float delta)
    {
        var snapshot = Snapshot();

        // Unity 会先跑完所有 Start，再进入 Update
        foreach (var b in snapshot)
        {
            if (b.IsDestroyed || !b.EnabledRaw || b.Started) continue;
            b.Started = true; // 先置位：Start 只尝试一次，抛异常也不会每帧重来
            try { InvokeOwned(b, b.OnStart); }
            catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnStart), ex); }
        }

        foreach (var b in snapshot)
        {
            if (b.IsDestroyed || !b.EnabledRaw || !b.Started) continue;
            try { InvokeOwned(b, () => b.OnUpdate(delta)); }
            catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnUpdate), ex); }
        }

        foreach (var b in snapshot)
        {
            if (b.IsDestroyed || !b.EnabledRaw || !b.Started) continue;
            try { InvokeOwned(b, () => b.OnLateUpdate(delta)); }
            catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnLateUpdate), ex); }
        }
    }

    /// <summary>对所有行为调用 OnGUI（ImGui 窗口渲染之后）。</summary>
    internal static void GUI(ImDrawListPtr drawList)
    {
        foreach (var b in Snapshot())
        {
            if (b.IsDestroyed || !b.EnabledRaw || !b.Started) continue;
            try { InvokeOwned(b, () => b.OnGUI(drawList)); }
            catch (Exception ex) { LogFault(b, nameof(GameBehaviour.OnGUI), ex); }
        }
    }
}

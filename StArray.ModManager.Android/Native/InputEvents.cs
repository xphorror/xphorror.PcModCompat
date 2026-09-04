using System.Diagnostics;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Android.Native;

/// <summary>一次完整触摸事件的值类型快照。</summary>
/// <param name="Action">已去除指针索引位的主动作</param>
/// <param name="PointerIndex">本次动作对应的指针索引</param>
/// <param name="PointerId">可跨事件跟踪的稳定指针 ID</param>
/// <param name="EventTimeNanos">CLOCK_MONOTONIC 纳秒时间戳</param>
/// <param name="X">触点 X 坐标，单位为像素</param>
/// <param name="Y">触点 Y 坐标，单位为像素</param>
public readonly record struct TouchEventInfo(
    AndroidInput.MotionAction Action,
    int PointerIndex,
    int PointerId,
    long EventTimeNanos,
    float X,
    float Y)
{
    /// <summary>保留首版广播 API 的源码兼容构造函数。</summary>
    public TouchEventInfo(
        AndroidInput.MotionAction action,
        int pointerIndex,
        long eventTimeNanos,
        float x,
        float y)
        : this(action, pointerIndex, -1, eventTimeNanos, x, y)
    {
    }
}

/// <summary>异步输入使用的不含坐标的触摸时间戳快照。</summary>
public readonly record struct TouchTimestampInfo(
    AndroidInput.MotionAction Action,
    int PointerId,
    long EventTimeNanos);

/// <summary>
/// Android 原生触摸广播。回调位于输入分发线程，只能复制值或入队，不能访问 Unity 对象。
/// </summary>
public static class InputEvents
{
    private const long DuplicateWindowMilliseconds = 8L;
    private const int InitialFaultLogBudget = 8;
    private static readonly object SubscriptionSync = new();
    private static readonly object DedupSync = new();

    private static TouchSubscription[] s_touchSubscriptions = [];
    private static TimestampSubscription[] s_timestampSubscriptions = [];
    private static long s_nextSubscriptionId;
    private static int s_faultLogBudget = InitialFaultLogBudget;

    private static int s_lastRawAction;
    private static int s_lastPointerIndex;
    private static int s_lastPointerCount;
    private static int s_lastPointerId;
    private static long s_lastEventTimeNanos;
    private static long s_lastDispatchTicks;

    /// <summary>是否存在完整触摸或时间戳订阅者。</summary>
    public static bool HasSubscribers =>
        Volatile.Read(ref s_touchSubscriptions).Length != 0 ||
        Volatile.Read(ref s_timestampSubscriptions).Length != 0;

    /// <summary>完整触摸广播，包含 Move、坐标和稳定指针 ID。</summary>
    public static event Action<TouchEventInfo>? OnTouch
    {
        add
        {
            if (value != null)
                AddTouch(value);
        }
        remove
        {
            if (value != null)
                RemoveTouch(value);
        }
    }

    /// <summary>Down、PointerDown、Up、PointerUp 和 Cancel 的低开销时间戳广播。</summary>
    public static event Action<TouchTimestampInfo>? OnTouchTimestamp
    {
        add
        {
            if (value != null)
                AddTimestamp(value);
        }
        remove
        {
            if (value != null)
                RemoveTimestamp(value);
        }
    }

    /// <summary>解析一次原生事件并广播给当前有效的订阅 generation。</summary>
    internal static void RaiseFrom(nint inputEvent)
    {
        var touchSubscriptions = Volatile.Read(ref s_touchSubscriptions);
        var timestampSubscriptions = Volatile.Read(ref s_timestampSubscriptions);
        if ((touchSubscriptions.Length == 0 && timestampSubscriptions.Length == 0) ||
            inputEvent == 0)
        {
            return;
        }

        try
        {
            if (AndroidInput.AInputEvent_getType(inputEvent) != AndroidInput.EventType.Motion)
                return;

            var rawAction = AndroidInput.AMotionEvent_getAction(inputEvent);
            var action = AndroidInput.GetMainAction(rawAction);
            var timestampAction = IsTimestampAction(action);

            // The timestamp-only channel never inspects Move coordinates or pointer state.
            if (!timestampAction && touchSubscriptions.Length == 0)
                return;

            var pointerIndex = AndroidInput.GetPointerIndex(rawAction);
            var pointerCount = AndroidInput.AMotionEvent_getPointerCount(inputEvent);
            var eventTimeNanos = AndroidInput.AMotionEvent_getEventTime(inputEvent);
            var pointerId = -1;
            var coordinateIndex = pointerIndex;

            if (action != AndroidInput.MotionAction.Cancel)
            {
                if (pointerCount <= 0 || pointerIndex < 0 || pointerIndex >= pointerCount)
                    return;
                pointerId = AndroidInput.AMotionEvent_getPointerId(inputEvent, pointerIndex);
            }
            else if (pointerCount > 0 && (coordinateIndex < 0 || coordinateIndex >= pointerCount))
            {
                coordinateIndex = 0;
            }

            if (IsDuplicate(
                    rawAction,
                    pointerIndex,
                    pointerCount,
                    pointerId,
                    eventTimeNanos))
            {
                return;
            }

            if (timestampAction && timestampSubscriptions.Length != 0)
            {
                DispatchTimestamp(
                    timestampSubscriptions,
                    new TouchTimestampInfo(action, pointerId, eventTimeNanos));
            }

            if (touchSubscriptions.Length == 0)
                return;

            var x = 0f;
            var y = 0f;
            if (pointerCount > 0)
            {
                x = AndroidInput.AMotionEvent_getX(inputEvent, coordinateIndex);
                y = AndroidInput.AMotionEvent_getY(inputEvent, coordinateIndex);
            }

            DispatchTouch(
                touchSubscriptions,
                new TouchEventInfo(action, pointerIndex, pointerId, eventTimeNanos, x, y));
        }
        catch (Exception exception)
        {
            LogBounded("读取原生触摸事件失败", "host:InputEvents", exception);
        }
    }

    private static bool IsTimestampAction(AndroidInput.MotionAction action) =>
        action is AndroidInput.MotionAction.Down
            or AndroidInput.MotionAction.PointerDown
            or AndroidInput.MotionAction.Up
            or AndroidInput.MotionAction.PointerUp
            or AndroidInput.MotionAction.Cancel;

    private static bool IsDuplicate(
        int rawAction,
        int pointerIndex,
        int pointerCount,
        int pointerId,
        long eventTimeNanos)
    {
        var now = Stopwatch.GetTimestamp();
        var windowTicks = Math.Max(
            1L,
            Stopwatch.Frequency * DuplicateWindowMilliseconds / 1000L);

        lock (DedupSync)
        {
            var elapsed = now - s_lastDispatchTicks;
            var duplicate = s_lastRawAction == rawAction &&
                            s_lastPointerIndex == pointerIndex &&
                            s_lastPointerCount == pointerCount &&
                            s_lastPointerId == pointerId &&
                            s_lastEventTimeNanos == eventTimeNanos &&
                            elapsed >= 0L &&
                            elapsed <= windowTicks;
            if (duplicate)
                return true;

            s_lastRawAction = rawAction;
            s_lastPointerIndex = pointerIndex;
            s_lastPointerCount = pointerCount;
            s_lastPointerId = pointerId;
            s_lastEventTimeNanos = eventTimeNanos;
            s_lastDispatchTicks = now;
            return false;
        }
    }

    private static void AddTouch(Action<TouchEventInfo> handler)
    {
        var subscription = new TouchSubscription(
            NextSubscriptionId(),
            handler,
            HookHelper.CurrentOwnerId,
            HookHelper.CurrentRuntimeSession,
            HookHelper.CurrentRuntimeKey);
        BindRuntime(subscription, () => RemoveTouchExact(subscription));

        lock (SubscriptionSync)
        {
            var current = s_touchSubscriptions;
            var next = new TouchSubscription[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = subscription;
            Volatile.Write(ref s_touchSubscriptions, next);
        }

        EnsurePublishedSubscriptionIsActive(subscription, () => RemoveTouchExact(subscription));
    }

    private static void AddTimestamp(Action<TouchTimestampInfo> handler)
    {
        var subscription = new TimestampSubscription(
            NextSubscriptionId(),
            handler,
            HookHelper.CurrentOwnerId,
            HookHelper.CurrentRuntimeSession,
            HookHelper.CurrentRuntimeKey);
        BindRuntime(subscription, () => RemoveTimestampExact(subscription));

        lock (SubscriptionSync)
        {
            var current = s_timestampSubscriptions;
            var next = new TimestampSubscription[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = subscription;
            Volatile.Write(ref s_timestampSubscriptions, next);
        }

        EnsurePublishedSubscriptionIsActive(subscription, () => RemoveTimestampExact(subscription));
    }

    private static void BindRuntime(Subscription subscription, Action terminalCleanup)
    {
        if (!subscription.RuntimeKey.IsValid)
            return;
        if (subscription.RuntimeSession == null ||
            !string.Equals(
                subscription.Owner,
                subscription.RuntimeKey.OwnerId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "输入订阅的 MOD owner 与 runtime generation 不一致。");
        }

        if (!subscription.RuntimeSession.TryRegisterTerminalCleanup(
                subscription.RuntimeKey,
                terminalCleanup,
                out var terminalRegistration) ||
            terminalRegistration == null)
        {
            throw new InvalidOperationException("当前 MOD generation 已停止接受输入订阅。");
        }

        subscription.TerminalRegistration = terminalRegistration;
        if (ModOwnedResourceRegistry.TryRegister(
                subscription.RuntimeKey,
                ModOwnedResourceKind.InputSubscription,
                subscription.ResourceIdentity,
                ModOwnedResourceRetirementPolicy.RetainWhileSuspended))
        {
            return;
        }

        terminalRegistration.Dispose();
        subscription.TerminalRegistration = null;
        throw new InvalidOperationException("输入订阅资源登记失败。");
    }

    private static void EnsurePublishedSubscriptionIsActive(
        Subscription subscription,
        Action removeExact)
    {
        if (subscription.TerminalRegistration is { IsActive: false })
            removeExact();
    }

    private static void RemoveTouch(Action<TouchEventInfo> handler)
    {
        TouchSubscription? removed = null;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        lock (SubscriptionSync)
        {
            var current = s_touchSubscriptions;
            for (var index = current.Length - 1; index >= 0; --index)
            {
                if (!current[index].Handler.Equals(handler) ||
                    !CanRemove(current[index], runtimeKey))
                {
                    continue;
                }

                removed = current[index];
                Volatile.Write(ref s_touchSubscriptions, RemoveAt(current, index));
                break;
            }
        }

        if (removed != null)
            FinalizeRemoval(removed, disposeTerminalRegistration: true);
    }

    private static void RemoveTimestamp(Action<TouchTimestampInfo> handler)
    {
        TimestampSubscription? removed = null;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        lock (SubscriptionSync)
        {
            var current = s_timestampSubscriptions;
            for (var index = current.Length - 1; index >= 0; --index)
            {
                if (!current[index].Handler.Equals(handler) ||
                    !CanRemove(current[index], runtimeKey))
                {
                    continue;
                }

                removed = current[index];
                Volatile.Write(ref s_timestampSubscriptions, RemoveAt(current, index));
                break;
            }
        }

        if (removed != null)
            FinalizeRemoval(removed, disposeTerminalRegistration: true);
    }

    private static void RemoveTouchExact(TouchSubscription subscription)
    {
        var removed = false;
        lock (SubscriptionSync)
        {
            var current = s_touchSubscriptions;
            var index = Array.IndexOf(current, subscription);
            if (index >= 0)
            {
                Volatile.Write(ref s_touchSubscriptions, RemoveAt(current, index));
                removed = true;
            }
        }

        if (removed)
            FinalizeRemoval(subscription, disposeTerminalRegistration: false);
    }

    private static void RemoveTimestampExact(TimestampSubscription subscription)
    {
        var removed = false;
        lock (SubscriptionSync)
        {
            var current = s_timestampSubscriptions;
            var index = Array.IndexOf(current, subscription);
            if (index >= 0)
            {
                Volatile.Write(ref s_timestampSubscriptions, RemoveAt(current, index));
                removed = true;
            }
        }

        if (removed)
            FinalizeRemoval(subscription, disposeTerminalRegistration: false);
    }

    private static bool CanRemove(Subscription subscription, ModRuntimeKey currentKey) =>
        !currentKey.IsValid ||
        (subscription.RuntimeKey.IsValid && subscription.RuntimeKey.Matches(currentKey));

    private static T[] RemoveAt<T>(T[] source, int index)
    {
        if (source.Length == 1)
            return [];
        var result = new T[source.Length - 1];
        if (index > 0)
            Array.Copy(source, 0, result, 0, index);
        if (index < source.Length - 1)
            Array.Copy(source, index + 1, result, index, source.Length - index - 1);
        return result;
    }

    private static void FinalizeRemoval(
        Subscription subscription,
        bool disposeTerminalRegistration)
    {
        if (disposeTerminalRegistration)
            subscription.TerminalRegistration?.Dispose();
        if (subscription.RuntimeKey.IsValid)
        {
            ModOwnedResourceRegistry.RetireExact(
                subscription.RuntimeKey,
                ModOwnedResourceKind.InputSubscription,
                subscription.ResourceIdentity);
        }
    }

    private static void DispatchTouch(
        IReadOnlyList<TouchSubscription> subscriptions,
        TouchEventInfo info)
    {
        foreach (var subscription in subscriptions)
            Invoke(subscription, info);
    }

    private static void DispatchTimestamp(
        IReadOnlyList<TimestampSubscription> subscriptions,
        TouchTimestampInfo info)
    {
        foreach (var subscription in subscriptions)
            Invoke(subscription, info);
    }

    private static void Invoke<T>(Subscription<T> subscription, T info)
    {
        var enteredRuntime = false;
        IDisposable? ownerScope = null;
        try
        {
            if (subscription.RuntimeSession != null)
            {
                if (!subscription.RuntimeSession.TryEnterCallbackFast(subscription.RuntimeKey))
                    return;
                enteredRuntime = true;
                ownerScope = HookHelper.EnterOwnerScope(
                    subscription.RuntimeKey.OwnerId,
                    subscription.RuntimeSession,
                    subscription.RuntimeKey);
            }

            subscription.Handler(info);
        }
        catch (Exception exception)
        {
            LogBounded("触摸订阅回调失败", subscription.Owner, exception);
        }
        finally
        {
            try
            {
                ownerScope?.Dispose();
            }
            catch (Exception exception)
            {
                LogBounded("触摸订阅 owner scope 清理失败", subscription.Owner, exception);
            }

            if (enteredRuntime)
                subscription.RuntimeSession!.ExitCallbackFast(subscription.RuntimeKey);
        }
    }

    private static long NextSubscriptionId()
    {
        var id = Interlocked.Increment(ref s_nextSubscriptionId);
        if (id <= 0)
            throw new InvalidOperationException("输入订阅序号已耗尽。");
        return id;
    }

    private static void LogBounded(string message, string owner, Exception exception)
    {
        if (Interlocked.Decrement(ref s_faultLogBudget) < 0)
            return;
        try
        {
            Logger.Error(nameof(InputEvents), $"{message} owner={owner}: {exception}");
        }
        catch
        {
            // Native input dispatch must never receive a managed logging exception.
        }
    }

    internal static void RaiseForTests(TouchEventInfo info, bool includeTimestamp = true)
    {
        if (includeTimestamp && IsTimestampAction(info.Action))
        {
            DispatchTimestamp(
                Volatile.Read(ref s_timestampSubscriptions),
                new TouchTimestampInfo(info.Action, info.PointerId, info.EventTimeNanos));
        }
        DispatchTouch(Volatile.Read(ref s_touchSubscriptions), info);
    }

    internal static bool IsDuplicateForTests(
        int rawAction,
        int pointerIndex,
        int pointerCount,
        int pointerId,
        long eventTimeNanos)
        => IsDuplicate(rawAction, pointerIndex, pointerCount, pointerId, eventTimeNanos);

    internal static void ResetForTests()
    {
        TouchSubscription[] touch;
        TimestampSubscription[] timestamp;
        lock (SubscriptionSync)
        {
            touch = s_touchSubscriptions;
            timestamp = s_timestampSubscriptions;
            Volatile.Write(ref s_touchSubscriptions, []);
            Volatile.Write(ref s_timestampSubscriptions, []);
        }

        foreach (var subscription in touch.Cast<Subscription>().Concat(timestamp))
            FinalizeRemoval(subscription, disposeTerminalRegistration: true);

        lock (DedupSync)
        {
            s_lastRawAction = 0;
            s_lastPointerIndex = 0;
            s_lastPointerCount = 0;
            s_lastPointerId = 0;
            s_lastEventTimeNanos = 0;
            s_lastDispatchTicks = 0;
        }
        s_nextSubscriptionId = 0;
        s_faultLogBudget = InitialFaultLogBudget;
    }

    private abstract class Subscription(
        long id,
        Delegate handler,
        string? owner,
        ModRuntimeSession? runtimeSession,
        ModRuntimeKey runtimeKey)
    {
        public long Id { get; } = id;
        public Delegate UntypedHandler { get; } = handler;
        public string Owner { get; } = string.IsNullOrWhiteSpace(owner)
            ? runtimeKey.IsValid ? runtimeKey.OwnerId : "host:InputEvents"
            : owner;
        public ModRuntimeSession? RuntimeSession { get; } = runtimeSession;
        public ModRuntimeKey RuntimeKey { get; } = runtimeKey;
        public IModRuntimeTerminalCleanupRegistration? TerminalRegistration { get; set; }
        public abstract string Channel { get; }
        public string ResourceIdentity =>
            $"channel={Channel};subscription={Id};handler={HandlerIdentity(UntypedHandler)}";
    }

    private abstract class Subscription<T>(
        long id,
        Action<T> handler,
        string? owner,
        ModRuntimeSession? runtimeSession,
        ModRuntimeKey runtimeKey)
        : Subscription(id, handler, owner, runtimeSession, runtimeKey)
    {
        public Action<T> Handler { get; } = handler;
    }

    private sealed class TouchSubscription(
        long id,
        Action<TouchEventInfo> handler,
        string? owner,
        ModRuntimeSession? runtimeSession,
        ModRuntimeKey runtimeKey)
        : Subscription<TouchEventInfo>(id, handler, owner, runtimeSession, runtimeKey)
    {
        public override string Channel => "touch";
    }

    private sealed class TimestampSubscription(
        long id,
        Action<TouchTimestampInfo> handler,
        string? owner,
        ModRuntimeSession? runtimeSession,
        ModRuntimeKey runtimeKey)
        : Subscription<TouchTimestampInfo>(id, handler, owner, runtimeSession, runtimeKey)
    {
        public override string Channel => "timestamp";
    }

    private static string HandlerIdentity(Delegate handler)
    {
        var method = handler.Method;
        var identity = $"{method.DeclaringType?.FullName ?? "<global>"}.{method.Name}";
        return identity.Replace('\r', ' ').Replace('\n', ' ');
    }
}

using System.Runtime.CompilerServices;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Host-owned scheduling boundary used by rewritten Android Managed MOD assemblies.
/// Every scheduled callback is bound to the generation which created it.
/// </summary>
public static class ModRuntimeAsyncBridge
{
    private static readonly ConditionalWeakTable<Thread, OwnedThread> Threads = new();
    private static readonly ConditionalWeakTable<Timer, OwnedTimer> Timers = new();
    private static readonly ConditionalWeakTable<PeriodicTimer, OwnedPeriodicTimer>
        PeriodicTimers = new();

    public static Task TrackTask(Task task, string operationName)
    {
        ArgumentNullException.ThrowIfNull(task);
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        if (task.IsCompleted)
            return task;

        var operation = runtime.Begin(operationName);
        ReleaseWhenComplete(task, operation);
        return task;
    }

    public static Task<T> TrackTaskOfT<T>(Task<T> task, string operationName)
    {
        ArgumentNullException.ThrowIfNull(task);
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        if (task.IsCompleted)
            return task;

        var operation = runtime.Begin(operationName);
        ReleaseWhenComplete(task, operation);
        return task;
    }

    public static void RequireCurrentScope() => _ = ModRuntimeCapturedScope.Capture("Managed scheduling");

    public static Task RunAction(Action action) =>
        RunCore(action, CancellationToken.None);

    public static Task RunActionCancelable(Action action, CancellationToken cancellationToken) =>
        RunCore(action, cancellationToken);

    public static Task RunTask(Func<Task> action) =>
        RunCore(action, CancellationToken.None);

    public static Task RunTaskCancelable(
        Func<Task> action,
        CancellationToken cancellationToken) =>
        RunCore(action, cancellationToken);

    public static Task<T> RunResult<T>(Func<T> action) =>
        RunCore(action, CancellationToken.None);

    public static Task<T> RunResultCancelable<T>(
        Func<T> action,
        CancellationToken cancellationToken) =>
        RunCore(action, cancellationToken);

    public static Task<T> RunTaskResult<T>(Func<Task<T>> action) =>
        RunCore(action, CancellationToken.None);

    public static Task<T> RunTaskResultCancelable<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken) =>
        RunCore(action, cancellationToken);

    public static Thread CreateThread(ThreadStart start) =>
        CreateOwnedThread(start, null);

    public static Thread CreateThreadWithStack(ThreadStart start, int maxStackSize) =>
        CreateOwnedThread(start, maxStackSize);

    public static Thread CreateParameterizedThread(ParameterizedThreadStart start) =>
        CreateOwnedThread(start, null);

    public static Thread CreateParameterizedThreadWithStack(
        ParameterizedThreadStart start,
        int maxStackSize) =>
        CreateOwnedThread(start, maxStackSize);

    public static void StartThread(Thread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var owned = RequireOwnedThread(thread);
        owned.ValidateCaller();
        owned.Start(null, parameterized: false);
    }

    public static void StartParameterizedThread(Thread thread, object? parameter)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var owned = RequireOwnedThread(thread);
        owned.ValidateCaller();
        owned.Start(parameter, parameterized: true);
    }

    public static bool QueueWaitCallback(WaitCallback callback) =>
        QueueWaitCallbackState(callback, null);

    public static bool QueueWaitCallbackState(WaitCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var operation = BeginOperation("thread-pool");
        try
        {
            var queued = ThreadPool.QueueUserWorkItem(
                static boxed =>
                {
                    var work = (WaitCallbackWork)boxed!;
                    try
                    {
                        using (work.Operation.EnterScope())
                            work.Callback(work.State);
                    }
                    finally
                    {
                        work.Operation.Dispose();
                    }
                },
                new WaitCallbackWork(callback, state, operation));
            if (!queued)
                operation.Dispose();
            return queued;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    public static bool QueueAction<TState>(
        Action<TState> callback,
        TState state,
        bool preferLocal)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var operation = BeginOperation("thread-pool");
        try
        {
            var queued = ThreadPool.QueueUserWorkItem(
                static work =>
                {
                    try
                    {
                        using (work.Operation.EnterScope())
                            work.Callback(work.State);
                    }
                    finally
                    {
                        work.Operation.Dispose();
                    }
                },
                new ActionWork<TState>(callback, state, operation),
                preferLocal);
            if (!queued)
                operation.Dispose();
            return queued;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a synchronous integer range with the current MOD scope restored for every body
    /// invocation. The outer operation keeps the generation alive until Parallel.For returns;
    /// each body invocation gets its own operation so retirement cannot admit a late callback.
    /// </summary>
    public static ParallelLoopResult ParallelFor(
        int fromInclusive,
        int toExclusive,
        Action<int> body) =>
        ParallelForCore(fromInclusive, toExclusive, new ParallelOptions(), body);

    /// <summary>Generation-bound bridge for Parallel.For with caller-supplied options.</summary>
    public static ParallelLoopResult ParallelForWithOptions(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Action<int> body) =>
        ParallelForCore(fromInclusive, toExclusive, parallelOptions, body);

    /// <summary>
    /// Runs a synchronous enumerable range with the current MOD scope restored for every
    /// body invocation. Only the callback shape is bridged; local-state and partitioner
    /// overloads remain unsupported until they have an equivalent generation contract.
    /// </summary>
    public static ParallelLoopResult ParallelForEach<T>(
        IEnumerable<T> source,
        Action<T> body) =>
        ParallelForEachCore(source, new ParallelOptions(), body);

    /// <summary>Generation-bound bridge for Parallel.ForEach with caller-supplied options.</summary>
    public static ParallelLoopResult ParallelForEachWithOptions<T>(
        IEnumerable<T> source,
        ParallelOptions parallelOptions,
        Action<T> body) =>
        ParallelForEachCore(source, parallelOptions, body);

    private static ParallelLoopResult ParallelForCore(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);

        return RunParallelCore(
            "parallel-for",
            parallelOptions,
            body,
            (options, callback) => Parallel.For(fromInclusive, toExclusive, options, callback));
    }

    private static ParallelLoopResult ParallelForEachCore<T>(
        IEnumerable<T> source,
        ParallelOptions parallelOptions,
        Action<T> body)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);

        return RunParallelCore(
            "parallel-foreach",
            parallelOptions,
            body,
            (options, callback) => Parallel.ForEach(source, options, callback));
    }

    private static ParallelLoopResult RunParallelCore<T>(
        string operationName,
        ParallelOptions parallelOptions,
        Action<T> body,
        Func<ParallelOptions, Action<T>, ParallelLoopResult> runner)
    {
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        using var operation = runtime.Begin(operationName);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            parallelOptions.CancellationToken,
            operation.CancellationToken);

        var bridgedOptions = new ParallelOptions
        {
            CancellationToken = linkedCancellation.Token,
            MaxDegreeOfParallelism = parallelOptions.MaxDegreeOfParallelism
        };
        if (parallelOptions.TaskScheduler != null)
            bridgedOptions.TaskScheduler = parallelOptions.TaskScheduler;

        return runner(
            bridgedOptions,
            item =>
            {
                ModRuntimeOwnedOperation callbackOperation;
                try
                {
                    callbackOperation = runtime.Begin("parallel-callback");
                }
                catch (InvalidOperationException) when (
                    operation.CancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(operation.CancellationToken);
                }

                try
                {
                    using (callbackOperation.EnterScope())
                        body(item);
                }
                finally
                {
                    callbackOperation.Dispose();
                }
            });
    }

    public static Timer CreateTimer(TimerCallback callback) =>
        CreateOwnedTimer(callback, static wrapped => new Timer(wrapped));

    public static Timer CreateTimerInt32(
        TimerCallback callback,
        object? state,
        int dueTime,
        int period) =>
        CreateOwnedTimer(callback, wrapped => new Timer(wrapped, state, dueTime, period));

    public static Timer CreateTimerUInt32(
        TimerCallback callback,
        object? state,
        uint dueTime,
        uint period) =>
        CreateOwnedTimer(callback, wrapped => new Timer(wrapped, state, dueTime, period));

    public static Timer CreateTimerTimeSpan(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) =>
        CreateOwnedTimer(callback, wrapped => new Timer(wrapped, state, dueTime, period));

    public static void DisposeTimer(Timer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var owned = RequireOwnedTimer(timer);
        owned.ValidateCaller();
        owned.Dispose();
    }

    public static bool DisposeTimerWaitHandle(Timer timer, WaitHandle notifyObject)
    {
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(notifyObject);
        var owned = RequireOwnedTimer(timer);
        owned.ValidateCaller();
        return owned.Dispose(notifyObject);
    }

    public static ValueTask DisposeTimerAsync(Timer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var owned = RequireOwnedTimer(timer);
        owned.ValidateCaller();
        return owned.DisposeAsync();
    }

    public static PeriodicTimer CreatePeriodicTimer(TimeSpan period)
    {
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        var owned = new OwnedPeriodicTimer(runtime);
        PeriodicTimer? timer = null;
        try
        {
            timer = new PeriodicTimer(period);
            owned.Bind(timer);
            PeriodicTimers.Add(timer, owned);
            return timer;
        }
        catch
        {
            timer?.Dispose();
            owned.DisposeRegistration();
            throw;
        }
    }

    public static ValueTask<bool> WaitForNextTickAsync(PeriodicTimer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var owned = RequireOwnedPeriodicTimer(timer);
        owned.ValidateCaller();
        return owned.Wait(CancellationToken.None);
    }

    public static ValueTask<bool> WaitForNextTickAsyncCancelable(
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var owned = RequireOwnedPeriodicTimer(timer);
        owned.ValidateCaller();
        return owned.Wait(cancellationToken);
    }

    public static void DisposePeriodicTimer(PeriodicTimer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var owned = RequireOwnedPeriodicTimer(timer);
        owned.ValidateCaller();
        owned.Dispose();
    }

    private static ModRuntimeOwnedOperation BeginOperation(string name) =>
        ModRuntimeCapturedScope.Capture("Managed scheduling").Begin(name);

    private static Task RunCore(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var operation = BeginOperation("task-run");
        try
        {
            var task = Task.Run(
                () =>
                {
                    using (operation.EnterScope())
                        action();
                },
                cancellationToken);
            ReleaseWhenComplete(task, operation);
            return task;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private static Task RunCore(Func<Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var operation = BeginOperation("task-run-async");
        try
        {
            var task = Task.Run(
                () =>
                {
                    using (operation.EnterScope())
                        return action();
                },
                cancellationToken);
            ReleaseWhenComplete(task, operation);
            return task;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private static Task<T> RunCore<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var operation = BeginOperation("task-run-result");
        try
        {
            var task = Task.Run(
                () =>
                {
                    using (operation.EnterScope())
                        return action();
                },
                cancellationToken);
            ReleaseWhenComplete(task, operation);
            return task;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private static Task<T> RunCore<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var operation = BeginOperation("task-run-async-result");
        try
        {
            var task = Task.Run(
                () =>
                {
                    using (operation.EnterScope())
                        return action();
                },
                cancellationToken);
            ReleaseWhenComplete(task, operation);
            return task;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private static void ReleaseWhenComplete(Task task, ModRuntimeOwnedOperation operation)
    {
        try
        {
            _ = task.ContinueWith(
                static (_, state) => ((ModRuntimeOwnedOperation)state!).Dispose(),
                operation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private static Thread CreateOwnedThread(ThreadStart start, int? maxStackSize)
    {
        ArgumentNullException.ThrowIfNull(start);
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        OwnedThread? owned = null;
        var thread = maxStackSize.HasValue
            ? new Thread(() => owned!.Invoke(), maxStackSize.Value)
            : new Thread(() => owned!.Invoke());
        owned = new OwnedThread(thread, runtime, start, null);
        Threads.Add(thread, owned);
        return thread;
    }

    private static Thread CreateOwnedThread(ParameterizedThreadStart start, int? maxStackSize)
    {
        ArgumentNullException.ThrowIfNull(start);
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        OwnedThread? owned = null;
        var thread = maxStackSize.HasValue
            ? new Thread(state => owned!.Invoke(state), maxStackSize.Value)
            : new Thread(state => owned!.Invoke(state));
        owned = new OwnedThread(thread, runtime, null, start);
        Threads.Add(thread, owned);
        return thread;
    }

    private static OwnedThread RequireOwnedThread(Thread thread) =>
        Threads.TryGetValue(thread, out var owned)
            ? owned
            : throw new InvalidOperationException(
                "Thread was not created by the current MOD runtime bridge.");

    private static Timer CreateOwnedTimer(
        TimerCallback callback,
        Func<TimerCallback, Timer> factory)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var runtime = ModRuntimeCapturedScope.Capture("Managed scheduling");
        var owned = new OwnedTimer(runtime, callback);
        Timer? timer = null;
        try
        {
            timer = factory(owned.Invoke);
            owned.Bind(timer);
            Timers.Add(timer, owned);
            return timer;
        }
        catch
        {
            timer?.Dispose();
            owned.DisposeRegistration();
            throw;
        }
    }

    private static OwnedTimer RequireOwnedTimer(Timer timer) =>
        Timers.TryGetValue(timer, out var owned)
            ? owned
            : throw new InvalidOperationException(
                "Timer was not created by the current MOD runtime bridge.");

    private static OwnedPeriodicTimer RequireOwnedPeriodicTimer(PeriodicTimer timer) =>
        PeriodicTimers.TryGetValue(timer, out var owned)
            ? owned
            : throw new InvalidOperationException(
                "PeriodicTimer was not created by the current MOD runtime bridge.");

    private sealed record WaitCallbackWork(
        WaitCallback Callback,
        object? State,
        ModRuntimeOwnedOperation Operation);

    private sealed record ActionWork<TState>(
        Action<TState> Callback,
        TState State,
        ModRuntimeOwnedOperation Operation);

    private sealed class OwnedThread(
        Thread thread,
        ModRuntimeCapturedScope runtime,
        ThreadStart? start,
        ParameterizedThreadStart? parameterizedStart)
    {
        private readonly object _sync = new();
        private readonly Thread _thread = thread;
        private ModRuntimeOwnedOperation? _operation;
        private int _started;

        internal void Start(object? parameter, bool parameterized)
        {
            if ((parameterizedStart != null) != parameterized)
                throw new InvalidOperationException("Thread.Start overload does not match its constructor.");
            lock (_sync)
            {
                if (_started != 0)
                    throw new ThreadStateException("Thread has already been started.");
                _operation = runtime.Begin("thread");
                _started = 1;
                try
                {
                    if (parameterized)
                        _thread.Start(parameter);
                    else
                        _thread.Start();
                }
                catch
                {
                    Interlocked.Exchange(ref _operation, null)?.Dispose();
                    throw;
                }
            }
        }

        internal void ValidateCaller() => runtime.ValidateCurrentCaller("Managed resource");

        internal void Invoke(object? parameter = null)
        {
            var operation = Volatile.Read(ref _operation)
                            ?? throw new InvalidOperationException(
                                "MOD thread entered without a generation operation.");
            try
            {
                using (operation.EnterScope())
                {
                    if (parameterizedStart != null)
                        parameterizedStart(parameter);
                    else
                        start!();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _operation, null)?.Dispose();
            }
        }
    }

    private sealed class OwnedTimer(
        ModRuntimeCapturedScope runtime,
        TimerCallback callback)
    {
        private readonly object _sync = new();
        private Timer? _timer;
        private IModRuntimeTerminalCleanupRegistration? _registration;
        private int _disposed;

        internal void ValidateCaller() => runtime.ValidateCurrentCaller("Managed resource");

        internal void Bind(Timer timer)
        {
            lock (_sync)
            {
                _timer = timer;
                if (!runtime.TryRegisterCleanup(DisposeFromHost, out _registration) ||
                    _registration == null)
                {
                    _timer = null;
                    throw new InvalidOperationException(
                        "Timer registration was rejected by the MOD runtime session.");
                }
            }
        }

        internal void Invoke(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            ModRuntimeOwnedOperation operation;
            try
            {
                operation = runtime.Begin("timer-callback");
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                using (operation.EnterScope())
                    callback(state);
            }
            finally
            {
                operation.Dispose();
            }
        }

        internal void Dispose()
        {
            var timer = BeginDispose();
            timer?.Dispose();
        }

        internal bool Dispose(WaitHandle notifyObject)
        {
            var timer = BeginDispose();
            return timer?.Dispose(notifyObject) ?? false;
        }

        internal ValueTask DisposeAsync()
        {
            var timer = BeginDispose();
            return timer?.DisposeAsync() ?? ValueTask.CompletedTask;
        }

        internal void DisposeRegistration() =>
            Interlocked.Exchange(ref _registration, null)?.Dispose();

        private Timer? BeginDispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return null;
            Timer? timer;
            lock (_sync)
            {
                timer = _timer;
                _timer = null;
            }
            DisposeRegistration();
            if (timer != null)
                Timers.Remove(timer);
            return timer;
        }

        private void DisposeFromHost()
        {
            var timer = BeginDispose();
            timer?.Dispose();
        }
    }

    private sealed class OwnedPeriodicTimer(ModRuntimeCapturedScope runtime)
    {
        private readonly object _sync = new();
        private PeriodicTimer? _timer;
        private IModRuntimeTerminalCleanupRegistration? _registration;
        private int _disposed;

        internal void ValidateCaller() => runtime.ValidateCurrentCaller("Managed resource");

        internal void Bind(PeriodicTimer timer)
        {
            lock (_sync)
            {
                _timer = timer;
                if (!runtime.TryRegisterCleanup(DisposeFromHost, out _registration) ||
                    _registration == null)
                {
                    _timer = null;
                    throw new InvalidOperationException(
                        "PeriodicTimer registration was rejected by the MOD runtime session.");
                }
            }
        }

        internal ValueTask<bool> Wait(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PeriodicTimer));

            PeriodicTimer timer;
            lock (_sync)
            {
                timer = _timer ?? throw new ObjectDisposedException(nameof(PeriodicTimer));
            }

            var operation = runtime.Begin("periodic-timer-wait");
            return Await(timer, cancellationToken, operation);
        }

        internal void Dispose()
        {
            var timer = BeginDispose();
            timer?.Dispose();
        }

        internal void DisposeRegistration() =>
            Interlocked.Exchange(ref _registration, null)?.Dispose();

        private static async ValueTask<bool> Await(
            PeriodicTimer timer,
            CancellationToken cancellationToken,
            ModRuntimeOwnedOperation operation)
        {
            try
            {
                using (operation.EnterScope())
                {
                    if (!cancellationToken.CanBeCanceled)
                        return await timer.WaitForNextTickAsync(operation.CancellationToken)
                            .ConfigureAwait(false);

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        operation.CancellationToken);
                    return await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                operation.Dispose();
            }
        }

        private PeriodicTimer? BeginDispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return null;

            PeriodicTimer? timer;
            lock (_sync)
            {
                timer = _timer;
                _timer = null;
            }
            DisposeRegistration();
            if (timer != null)
                PeriodicTimers.Remove(timer);
            return timer;
        }

        private void DisposeFromHost()
        {
            var timer = BeginDispose();
            timer?.Dispose();
        }
    }
}

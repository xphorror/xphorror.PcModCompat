using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class NativeModOperationLifecycleTests
{
    [SetUp]
    public void SetUp() => ModOwnedResourceRegistry.ClearForTests();

    [TearDown]
    public void TearDown() => ModOwnedResourceRegistry.ClearForTests();

    [Test]
    public void TokenLayoutMatchesNativeAbiV1()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<ModNativeOperationToken>(), Is.EqualTo(24));
            Assert.That(Marshal.OffsetOf<ModNativeOperationToken>(
                nameof(ModNativeOperationToken.AbiVersion)).ToInt32(), Is.Zero);
            Assert.That(Marshal.OffsetOf<ModNativeOperationToken>(
                nameof(ModNativeOperationToken.Slot)).ToInt32(), Is.EqualTo(4));
            Assert.That(Marshal.OffsetOf<ModNativeOperationToken>(
                nameof(ModNativeOperationToken.OperationId)).ToInt32(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf<ModNativeOperationToken>(
                nameof(ModNativeOperationToken.Cookie)).ToInt32(), Is.EqualTo(16));
        });
    }

    [Test]
    public void NativeRegistryIsConnectedBeforePluginCodeAndBeforeContextRelease()
    {
        var root = FindRepositoryRoot();
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Runtime",
            "ModLoader.cs"));
        var cmake = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "CMakeLists.txt"));
        var client = File.ReadAllText(Path.Combine(
            Directory.GetParent(root)!.FullName,
            "common",
            "modmanager_native_operation_client.h"));
        var beginLoad = loader.IndexOf(
            "runtimeKey = mod.RuntimeSession.BeginLoad",
            StringComparison.Ordinal);
        var openGeneration = loader.IndexOf(
            "HookHelper.OpenNativeOperationGeneration(runtimeKey)",
            beginLoad,
            StringComparison.Ordinal);
        var pluginOnLoad = loader.IndexOf(
            "plugin.OnLoad()",
            openGeneration,
            StringComparison.Ordinal);
        var retirement = loader.IndexOf(
            "mod.RuntimeSession.TryBeginRetirement(runtimeKey)",
            pluginOnLoad,
            StringComparison.Ordinal);
        var nativeQuiescence = loader.IndexOf(
            "EnsureNativeOperationsQuiesced(mod, runtimeKey)",
            retirement,
            StringComparison.Ordinal);
        var onUnload = loader.IndexOf(
            "mod.PluginInstance.OnUnload()",
            nativeQuiescence,
            StringComparison.Ordinal);
        var nativeRetirement = loader.IndexOf(
            "EnsureNativeOperationGenerationRetired(mod, runtimeKey)",
            onUnload,
            StringComparison.Ordinal);
        var releaseContext = loader.IndexOf(
            "nativeState.ReleaseContext()",
            nativeRetirement,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(cmake, Does.Contain("core/native_mod_operation_registry.cpp"));
            Assert.That(client, Does.Contain("modmanager_native_operation_begin_v1"));
            Assert.That(client, Does.Contain(
                "modmanager_native_operation_is_cancellation_requested_v1"));
            Assert.That(client, Does.Contain("modmanager_native_operation_end_v1"));
            Assert.That(openGeneration, Is.GreaterThan(beginLoad));
            Assert.That(pluginOnLoad, Is.GreaterThan(openGeneration),
                "Host must open the generation before any plugin lifecycle code runs.");
            Assert.That(nativeQuiescence, Is.GreaterThan(retirement));
            Assert.That(onUnload, Is.GreaterThan(nativeQuiescence),
                "Host must reject and drain native work before calling OnUnload.");
            Assert.That(nativeRetirement, Is.GreaterThan(onUnload));
            Assert.That(releaseContext, Is.GreaterThan(nativeRetirement),
                "Collectible ALC/native handles must not release before registry retirement.");
        });
    }

    [Test]
    public void PublicLeaseRequiresOpenCurrentGenerationAndRetiresResource()
    {
        var previous = HookHelper.Instance;
        var provider = new NativeOperationHookProvider();
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "native-operation-api");
        try
        {
            HookHelper.Instance = provider;
            Assert.That(ModNativeOperations.TryBegin("outside-owner", out _), Is.False);
            Assert.That(HookHelper.OpenNativeOperationGeneration(key), Is.True);

            IModNativeOperationLease? operation;
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                Assert.That(ModNativeOperations.TryBegin("private-worker", out operation), Is.True);

            Assert.That(operation, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(operation!.Token.IsValid, Is.True);
                Assert.That(operation.IsCancellationRequested, Is.False);
                Assert.That(HookHelper.GetActiveNativeOperationCount(key), Is.EqualTo(1));
                Assert.That(
                    ModOwnedResourceRegistry.Snapshot(key, includeRetired: false)
                        .Single().Kind,
                    Is.EqualTo(ModOwnedResourceKind.NativeOperation));
            });

            Assert.That(session.TryBeginRetirement(key), Is.True);
            Assert.That(HookHelper.CancelNativeOperationsAndWait(key, TimeSpan.Zero), Is.False);
            Assert.That(operation!.IsCancellationRequested, Is.True);
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                Assert.That(ModNativeOperations.TryBegin("late-worker", out _), Is.False);

            operation.Dispose();
            operation.Dispose();
            Assert.That(HookHelper.CancelNativeOperationsAndWait(key, TimeSpan.Zero), Is.True);
            Assert.That(HookHelper.RetireNativeOperationGeneration(key), Is.True);
            Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
            Assert.That(session.TryCompleteRetirement(key), Is.True);
            Assert.That(ModOwnedResourceRegistry.Snapshot(key, includeRetired: false), Is.Empty);
        }
        finally
        {
            HookHelper.Instance = previous;
        }
    }

    [Test]
    public void TimeoutRollbackKeepsOldTokenCancelledAndAcceptsFreshWork()
    {
        var previous = HookHelper.Instance;
        var provider = new NativeOperationHookProvider();
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "native-operation-rollback");
        Assert.That(session.TryPublishActive(key), Is.True);
        try
        {
            HookHelper.Instance = provider;
            Assert.That(HookHelper.OpenNativeOperationGeneration(key), Is.True);
            IModNativeOperationLease? slow;
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                Assert.That(ModNativeOperations.TryBegin("slow-worker", out slow), Is.True);

            Assert.That(session.TryBeginRetirement(key), Is.True);
            Assert.That(HookHelper.CancelNativeOperationsAndWait(key, TimeSpan.Zero), Is.False);
            Assert.That(session.TryCancelRetirement(
                key,
                ModRuntimeLifecycleState.Active), Is.True);
            Assert.That(HookHelper.ResumeNativeOperationGeneration(key), Is.True);

            IModNativeOperationLease? replacement;
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                Assert.That(ModNativeOperations.TryBegin("replacement", out replacement), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(slow!.IsCancellationRequested, Is.True);
                Assert.That(replacement!.IsCancellationRequested, Is.False);
                Assert.That(HookHelper.GetActiveNativeOperationCount(key), Is.EqualTo(2));
            });

            slow!.Dispose();
            slow.Dispose();
            Assert.That(HookHelper.GetActiveNativeOperationCount(key), Is.EqualTo(1));
            replacement!.Dispose();
            Assert.That(HookHelper.GetActiveNativeOperationCount(key), Is.Zero);
        }
        finally
        {
            HookHelper.Instance = previous;
        }
    }

    private sealed class NativeOperationHookProvider : IHook, INativeModOperationProvider
    {
        private enum Lifecycle
        {
            Active,
            Retiring,
            Retired,
        }

        private sealed class Operation(ModNativeOperationToken token)
        {
            public ModNativeOperationToken Token { get; } = token;
            public bool Cancelled { get; set; }
        }

        private readonly object _sync = new();
        private readonly Dictionary<(string Owner, long Generation), Lifecycle> _generations = new();
        private readonly Dictionary<ulong, Operation> _operations = new();
        private ulong _nextOperation;

        public nint Hook(nint target, nint detour) => target;
        public bool Unhook(nint target) => true;
        public nint GetFunction(string library, string name) => nint.Zero;

        public bool OpenGeneration(string owner, long generation)
        {
            lock (_sync)
            {
                var key = (owner, generation);
                if (_generations.TryGetValue(key, out var lifecycle))
                    return lifecycle == Lifecycle.Active;
                _generations[key] = Lifecycle.Active;
                return true;
            }
        }

        public bool TryBeginOperation(
            string owner,
            long generation,
            string name,
            out ModNativeOperationToken token)
        {
            lock (_sync)
            {
                if (!_generations.TryGetValue((owner, generation), out var lifecycle) ||
                    lifecycle != Lifecycle.Active)
                {
                    token = default;
                    return false;
                }
                var operationId = ++_nextOperation;
                token = new ModNativeOperationToken(
                    1,
                    (uint)(operationId % 256),
                    operationId,
                    operationId ^ 0xC0DEC0DEUL);
                _operations.Add(operationId, new Operation(token));
                return true;
            }
        }

        public int GetCancellationState(in ModNativeOperationToken token)
        {
            lock (_sync)
                return _operations.TryGetValue(token.OperationId, out var operation) &&
                       operation.Token.Cookie == token.Cookie
                    ? operation.Cancelled ? 1 : 0
                    : -1;
        }

        public bool EndOperation(in ModNativeOperationToken token)
        {
            lock (_sync)
            {
                return _operations.TryGetValue(token.OperationId, out var operation) &&
                       operation.Token.Cookie == token.Cookie &&
                       _operations.Remove(token.OperationId);
            }
        }

        public bool CancelGenerationAndWait(
            string owner,
            long generation,
            uint timeoutMilliseconds)
        {
            lock (_sync)
            {
                var key = (owner, generation);
                if (!_generations.TryGetValue(key, out var lifecycle))
                    return true;
                if (lifecycle == Lifecycle.Retired)
                    return CountActive() == 0;
                _generations[key] = Lifecycle.Retiring;
                foreach (var operation in _operations.Values)
                    operation.Cancelled = true;
                return CountActive() == 0;

                int CountActive() => _operations.Count;
            }
        }

        public bool ResumeGeneration(string owner, long generation)
        {
            lock (_sync)
            {
                var key = (owner, generation);
                if (!_generations.TryGetValue(key, out var lifecycle) ||
                    lifecycle == Lifecycle.Retired)
                {
                    return false;
                }
                _generations[key] = Lifecycle.Active;
                return true;
            }
        }

        public bool RetireGeneration(string owner, long generation)
        {
            lock (_sync)
            {
                var key = (owner, generation);
                if (!_generations.TryGetValue(key, out var lifecycle))
                    return true;
                if (lifecycle == Lifecycle.Active || _operations.Count != 0)
                    return false;
                _generations[key] = Lifecycle.Retired;
                return true;
            }
        }

        public int GetActiveOperationCount(string owner, long generation)
        {
            lock (_sync)
                return _operations.Count;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager repository root from test directory");
        return string.Empty;
    }
}

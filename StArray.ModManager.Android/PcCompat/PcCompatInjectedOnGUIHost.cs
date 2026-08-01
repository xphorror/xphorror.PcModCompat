using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Owns the real IL2CPP MonoBehaviour that gives managed MOD settings a Unity
/// IMGUI event context. The injected component is permanent; demand is gated in
/// managed code because injected IL2CPP classes and HookBroker layers are never
/// removed at runtime.
/// </summary>
public static class PcCompatInjectedOnGUIHost
{
    private const string LogTag = "PcCompatOnGUIHost";
    private const string HostTypeName =
        "Xphorror.PcModCompat.Injected.PcCompatOnGUIHost";
    private static readonly object Gate = new();
    private static int s_state;
    private static Type? s_hostType;
    private static HostUnityApi? s_api;
    private static object? s_root;
    private static object? s_component;
    private static long s_dispatchCount;
    private static int s_dispatchReady;
    private static int s_demand;
    private static int s_demandApplyQueued;
    private static string s_status = "not-installed";

    public static bool IsReady => Volatile.Read(ref s_state) == 3;
    public static bool IsDispatchReady => Volatile.Read(ref s_dispatchReady) != 0;

    internal static void Install()
    {
        lock (Gate)
        {
            if (s_state != 0)
                return;

            if (OperatingSystem.IsAndroid() &&
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                s_status =
                    "injected host not attempted: upstream ClassInjector internal resolver is x86/x64-only; " +
                    "native BeginGUI host fallback active";
                Volatile.Write(ref s_state, 4);
                Logger.Warn(LogTag, s_status);
                return;
            }

            try
            {
                var hostType = BuildHostType();
                ClassInjector.RegisterTypeInIl2Cpp(
                    hostType,
                    new RegisterTypeOptions { LogSuccess = false });
                s_hostType = hostType;
                s_status = "registered";
                Volatile.Write(ref s_state, 1);
                Logger.Info(LogTag, "injected MonoBehaviour type registered through HookBroker");
            }
            catch (Exception exception)
            {
                var root = exception.GetBaseException();
                s_status =
                    $"registration failed: {root.GetType().Name}: {root.Message}";
                Volatile.Write(ref s_state, 4);
                Logger.Error(LogTag, "injected MonoBehaviour type registration failed: " + exception);
                return;
            }
        }

        RequestInstall();
    }

    internal static void RequestInstall()
    {
        if (Volatile.Read(ref s_state) != 1)
            return;

        lock (Gate)
        {
            if (s_state != 1)
                return;
            s_status = "UnityMain install queued";
            Volatile.Write(ref s_state, 2);
        }

        if (PcCompatResourceBundleLoader.TryScheduleUnityMainWork(CreateOnUnityMain))
            return;

        lock (Gate)
        {
            if (s_state == 2)
            {
                s_status = "waiting for UnityMain queue";
                Volatile.Write(ref s_state, 1);
            }
        }
    }

    internal static void SetDemand(bool enabled)
    {
        Volatile.Write(ref s_demand, enabled ? 1 : 0);
        try
        {
            if (enabled && !IsReady)
                RequestInstall();
            ScheduleDemandApply();
        }
        catch (Exception exception)
        {
            Fail("OnGUI host demand update failed", exception);
        }
    }

    /// <summary>Called only by the injected IL2CPP OnGUI message thunk.</summary>
    public static void DispatchFromUnityMessage()
    {
        if (!IsDispatchReady)
            return;

        try
        {
            Interlocked.Increment(ref s_dispatchCount);
            PcCompatManagedSelfRenderBridge.DispatchOnGUIFromInjectedHost();
        }
        catch (Exception exception)
        {
            // No managed exception may unwind through the IL2CPP message thunk.
            Logger.Error(LogTag, "injected OnGUI dispatch failed closed: " + exception);
        }
    }

    internal static string GetDiagnostics()
        => $"state={Volatile.Read(ref s_state)} ready={IsReady} " +
           $"dispatchReady={IsDispatchReady} demand={Volatile.Read(ref s_demand) != 0} " +
           $"dispatches={Interlocked.Read(ref s_dispatchCount)} status={s_status}";

    private static void CreateOnUnityMain()
    {
        if (!PcCompatUnityMainExecutionContext.IsActive)
        {
            Fail("host creation was dispatched outside UnityMain", null);
            return;
        }

        Type hostType;
        lock (Gate)
        {
            if (s_state == 3 || s_state == 4)
                return;
            hostType = s_hostType ??
                       throw new InvalidOperationException("Injected OnGUI host type is unavailable.");
        }

        try
        {
            var api = new HostUnityApi();
            var root = api.CreateGameObject("__PcCompatOnGUIHost");
            var component = api.AddComponent(root, hostType);
            api.SetEnabled(component, false);
            api.DontDestroyOnLoad(root);

            lock (Gate)
            {
                s_api = api;
                s_root = root;
                s_component = component;
                s_status = "ready";
                Volatile.Write(ref s_state, 3);
            }

            ApplyDemandOnUnityMain();
            Logger.Info(LogTag, "persistent injected MonoBehaviour OnGUI host created disabled");
        }
        catch (Exception exception)
        {
            Fail("UnityMain host creation failed", exception);
        }
    }

    private static void ScheduleDemandApply()
    {
        if (!IsReady)
            return;
        if (PcCompatUnityMainExecutionContext.IsActive)
        {
            ApplyDemandOnUnityMain();
            return;
        }
        if (Interlocked.Exchange(ref s_demandApplyQueued, 1) != 0)
            return;
        if (PcCompatResourceBundleLoader.TryScheduleUnityMainWork(ApplyQueuedDemandOnUnityMain))
            return;
        Volatile.Write(ref s_demandApplyQueued, 0);
    }

    private static void ApplyQueuedDemandOnUnityMain()
    {
        try
        {
            ApplyDemandOnUnityMain();
        }
        finally
        {
            Volatile.Write(ref s_demandApplyQueued, 0);
            if (IsDispatchReady != (Volatile.Read(ref s_demand) != 0))
                ScheduleDemandApply();
        }
    }

    private static void ApplyDemandOnUnityMain()
    {
        if (!PcCompatUnityMainExecutionContext.IsActive)
            throw new InvalidOperationException("OnGUI host demand was applied outside UnityMain.");

        HostUnityApi api;
        object component;
        lock (Gate)
        {
            if (s_state != 3 || s_api is null || s_component is null)
                return;
            api = s_api;
            component = s_component;
        }

        var enabled = Volatile.Read(ref s_demand) != 0;
        var previous = Volatile.Read(ref s_dispatchReady) != 0;
        if (previous == enabled)
            return;

        api.SetEnabled(component, enabled);
        Volatile.Write(ref s_dispatchReady, enabled ? 1 : 0);
        if (enabled)
            PcCompatManagedSelfRenderBridge.NotifyInjectedOnGUIHostReady();
        Logger.Info(LogTag, "injected OnGUI component enabled=" + enabled);
    }

    private static void Fail(string message, Exception? exception)
    {
        var root = exception?.GetBaseException();
        lock (Gate)
        {
            s_status = root is null
                ? message
                : $"{message}: {root.GetType().Name}: {root.Message}";
            Volatile.Write(ref s_dispatchReady, 0);
            Volatile.Write(ref s_state, 4);
        }

        Logger.Error(
            LogTag,
            exception is null ? message : message + ": " + exception);
    }

    private static Type BuildHostType()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                out var monoBehaviourType))
        {
            throw new TypeLoadException("Generated UnityEngine.MonoBehaviour proxy is unavailable.");
        }

        var baseConstructor = monoBehaviourType.GetConstructor(
                                  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                  binder: null,
                                  [typeof(IntPtr)],
                                  modifiers: null)
                              ?? throw new MissingMethodException(
                                  monoBehaviourType.FullName,
                                  ".ctor(IntPtr)");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Xphorror.PcModCompat.InjectedUnityHosts"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("InjectedUnityHosts");
        var builder = module.DefineType(
            HostTypeName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            monoBehaviourType);

        var constructor = builder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(IntPtr)]);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldarg_1);
        constructorIl.Emit(OpCodes.Call, baseConstructor);
        constructorIl.Emit(OpCodes.Ret);

        var onGUI = builder.DefineMethod(
            "OnGUI",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            typeof(void),
            Type.EmptyTypes);
        var onGUIIl = onGUI.GetILGenerator();
        onGUIIl.Emit(
            OpCodes.Call,
            typeof(PcCompatInjectedOnGUIHost).GetMethod(
                nameof(DispatchFromUnityMessage),
                BindingFlags.Public | BindingFlags.Static)!);
        onGUIIl.Emit(OpCodes.Ret);

        return builder.CreateType() ??
               throw new TypeLoadException("Injected OnGUI host type creation returned null.");
    }

    private sealed class HostUnityApi
    {
        private readonly Type _objectType;
        private readonly Type _gameObjectType;
        private readonly Func<nint, object> _wrapBehaviour;
        private readonly Func<string, object> _createGameObject;
        private readonly Func<object, Il2CppSystem.Type, object?> _addComponent;
        private readonly Action<object, bool> _setEnabled;
        private readonly Action<object> _dontDestroyOnLoad;

        public HostUnityApi()
        {
            _objectType = RequiredType("UnityEngine.CoreModule", "UnityEngine.Object");
            _gameObjectType = RequiredType("UnityEngine.CoreModule", "UnityEngine.GameObject");
            var behaviourType = RequiredType("UnityEngine.CoreModule", "UnityEngine.Behaviour");
            var behaviourConstructor = behaviourType.GetConstructor([typeof(IntPtr)])
                                      ?? throw new MissingMethodException(
                                          behaviourType.FullName,
                                          ".ctor(IntPtr)");

            var constructor = _gameObjectType.GetConstructor([typeof(string)])
                              ?? throw new MissingMethodException(
                                  _gameObjectType.FullName,
                                  ".ctor(String)");
            var addComponent = _gameObjectType.GetMethod(
                                   "AddComponent",
                                   BindingFlags.Instance | BindingFlags.Public,
                                   binder: null,
                                   [typeof(Il2CppSystem.Type)],
                                   modifiers: null)
                               ?? throw new MissingMethodException(
                                   _gameObjectType.FullName,
                                   "AddComponent(Type)");
            var dontDestroyOnLoad = _objectType.GetMethod(
                                        "DontDestroyOnLoad",
                                        BindingFlags.Static | BindingFlags.Public,
                                        binder: null,
                                        [_objectType],
                                        modifiers: null)
                                    ?? throw new MissingMethodException(
                                        _objectType.FullName,
                                        "DontDestroyOnLoad(Object)");
            var setEnabled = behaviourType.GetMethod(
                                 "set_enabled",
                                 BindingFlags.Instance | BindingFlags.Public,
                                 binder: null,
                                 [typeof(bool)],
                                 modifiers: null)
                             ?? throw new MissingMethodException(
                                 behaviourType.FullName,
                                 "set_enabled(Boolean)");

            _createGameObject = CompileConstructor(constructor);
            _addComponent = CompileAddComponent(addComponent);
            _wrapBehaviour = CompilePointerConstructor(behaviourConstructor);
            _setEnabled = CompileSetEnabled(behaviourType, setEnabled);
            _dontDestroyOnLoad = CompileDontDestroyOnLoad(dontDestroyOnLoad);
        }

        public object CreateGameObject(string name)
        {
            var result = _createGameObject(name);
            RequirePointer(result, "GameObject constructor");
            return result;
        }

        public object AddComponent(object gameObject, Type componentType)
        {
            var result = _addComponent(gameObject, Il2CppType.From(componentType))
                         ?? throw new InvalidOperationException(
                             "GameObject.AddComponent returned null for injected OnGUI host.");
            RequirePointer(result, "GameObject.AddComponent");
            return _wrapBehaviour(((Il2CppObjectBase)result).Pointer);
        }

        public void DontDestroyOnLoad(object gameObject)
            => _dontDestroyOnLoad(gameObject);

        public void SetEnabled(object component, bool enabled)
            => _setEnabled(component, enabled);

        private static Func<string, object> CompileConstructor(ConstructorInfo constructor)
        {
            var name = Expression.Parameter(typeof(string), "name");
            return Expression.Lambda<Func<string, object>>(
                Expression.Convert(Expression.New(constructor, name), typeof(object)),
                name).Compile();
        }

        private static Func<nint, object> CompilePointerConstructor(
            ConstructorInfo constructor)
        {
            var pointer = Expression.Parameter(typeof(nint), "pointer");
            return Expression.Lambda<Func<nint, object>>(
                Expression.Convert(Expression.New(constructor, pointer), typeof(object)),
                pointer).Compile();
        }

        private Func<object, Il2CppSystem.Type, object?> CompileAddComponent(MethodInfo method)
        {
            var gameObject = Expression.Parameter(typeof(object), "gameObject");
            var componentType = Expression.Parameter(typeof(Il2CppSystem.Type), "componentType");
            return Expression.Lambda<Func<object, Il2CppSystem.Type, object?>>(
                Expression.Convert(
                    Expression.Call(
                        Expression.Convert(gameObject, _gameObjectType),
                        method,
                        componentType),
                    typeof(object)),
                gameObject,
                componentType).Compile();
        }

        private Action<object> CompileDontDestroyOnLoad(MethodInfo method)
        {
            var gameObject = Expression.Parameter(typeof(object), "gameObject");
            return Expression.Lambda<Action<object>>(
                Expression.Call(
                    method,
                    Expression.Convert(gameObject, _objectType)),
                gameObject).Compile();
        }

        private static Action<object, bool> CompileSetEnabled(
            Type behaviourType,
            MethodInfo method)
        {
            var component = Expression.Parameter(typeof(object), "component");
            var enabled = Expression.Parameter(typeof(bool), "enabled");
            return Expression.Lambda<Action<object, bool>>(
                Expression.Call(
                    Expression.Convert(component, behaviourType),
                    method,
                    enabled),
                component,
                enabled).Compile();
        }

        private static void RequirePointer(object value, string operation)
        {
            if (value is not Il2CppObjectBase proxy || proxy.Pointer == nint.Zero)
                throw new InvalidOperationException(operation + " returned an invalid IL2CPP object.");
        }

        private static Type RequiredType(string assemblyName, string fullTypeName)
            => PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                assemblyName,
                fullTypeName,
                out var type)
                ? type
                : throw new TypeLoadException(
                    $"Generated proxy type is unavailable: {assemblyName}:{fullTypeName}");
    }
}

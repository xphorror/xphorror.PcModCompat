using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Startup;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatIl2CppInteropBootstrap
{
    private const string LogTag = "PcCompatInterop";
    private static int s_state;
    private static Exception? s_failure;
    private static readonly Dictionary<string, Assembly> ProxyAssemblies =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PreferredProxyLoadOrder =
    [
        "UnityEngine.CoreModule.dll",
        "UnityEngine.UIModule.dll",
        "UnityEngine.AudioModule.dll",
        "UnityEngine.TextRenderingModule.dll",
        "UnityEngine.TextCoreFontEngineModule.dll",
        "UnityEngine.TextCoreTextEngineModule.dll",
        "UnityEngine.AssetBundleModule.dll",
        "UnityEngine.IMGUIModule.dll",
        "UnityEngine.InputLegacyModule.dll",
        "UnityEngine.UI.dll",
        "Unity.TextMeshPro.dll",
        "RDTools.dll",
        "Assembly-CSharp.dll"
    ];

    public static bool IsReady => Volatile.Read(ref s_state) == 2;

    public static void RequireReady()
    {
        var state = Volatile.Read(ref s_state);
        if (state == 2)
            return;
        var failure = Volatile.Read(ref s_failure);
        throw state switch
        {
            0 => new InvalidOperationException("Il2CppInterop generated proxies have not been started."),
            1 => new InvalidOperationException("Il2CppInterop generated proxies are still initializing."),
            3 when failure != null => new InvalidOperationException(
                "Il2CppInterop generated proxy initialization failed: " + failure.Message,
                failure),
            _ => new InvalidOperationException("Il2CppInterop generated proxies are not ready.")
        };
    }

    public static bool TryStart()
    {
        var previous = Interlocked.CompareExchange(ref s_state, 1, 0);
        if (previous != 0)
            return previous == 2;

        try
        {
            Volatile.Write(ref s_failure, null);
            AndroidGameAssemblyResolver.EnsureInstalled();
            if (!AndroidGameAssemblyResolver.WaitForHandle(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(
                    "IL2CPP handle is unavailable after Android resolver registration.");
            if (IL2CPP.il2cpp_domain_get() == nint.Zero)
                throw new InvalidOperationException("IL2CPP domain is unavailable after resolver registration.");
            ValidateRuntimeCorlib();
            Il2CppInteropRuntime.Create(new RuntimeConfiguration
            {
                UnityVersion = new Version(6000, 3, 10),
                DetourProvider = HookBrokerDetourProvider.Instance,
                EnableXrefScanner = false,
                EnableClassInjection = true
            }).Start();
            LoadAndValidateProxyAssemblies();

            Volatile.Write(ref s_state, 2);
            Logger.Info(
                LogTag,
                $"runtime initialized unity=6000.3.10f1 xref=off class-injection=on detours=hook-broker " +
                $"delegates=rooted proxies={ProxyAssemblies.Count} corlib=generated");
            return true;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("resolver", StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref s_failure, exception);
            Volatile.Write(ref s_state, 3);
            Logger.Error(LogTag, $"native resolver registration failed: {exception}");
            return false;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref s_failure, exception);
            Volatile.Write(ref s_state, 3);
            Logger.Error(LogTag, $"runtime initialization failed: {exception}");
            return false;
        }
    }

    private static void ValidateRuntimeCorlib()
    {
        var objectType = typeof(Il2CppSystem.Object);
        var pointerConstructor = objectType.GetConstructor([typeof(IntPtr)])
            ?? throw new MissingMethodException(objectType.FullName, ".ctor(IntPtr)");
        var il = pointerConstructor.GetMethodBody()?.GetILAsByteArray() ?? [];
        if (il.Length <= 3 || !il.Contains((byte)0x2A))
        {
            throw new InvalidOperationException(
                "Runtime Il2Cppmscorlib is the compile-time throw-null reference assembly.");
        }

        var nullableType = typeof(Il2CppSystem.Nullable<>);
        if (!nullableType.GetConstructors().Any(constructor =>
                constructor.GetParameters() is [{ ParameterType.IsGenericParameter: true }]))
        {
            throw new MissingMethodException(nullableType.FullName, ".ctor(T)");
        }

        var listType = typeof(Il2CppSystem.Collections.Generic.List<>);
        if (listType.GetProperty("Count")?.GetMethod == null)
            throw new MissingMemberException(listType.FullName, "Count");
        if (listType.GetProperty("Item")?.GetMethod == null)
            throw new MissingMemberException(listType.FullName, "Item");
        ValidateGenericProxyInitializer(listType);

        var classPointerStore = typeof(Il2CppClassPointerStore<>);
        if (classPointerStore.GetMethod(
                "GetNativeClassPointerForGenericArgument",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
        {
            throw new MissingMethodException(
                classPointerStore.FullName,
                "GetNativeClassPointerForGenericArgument");
        }

        foreach (var helperName in new[]
                 {
                     "RequireIl2CppClass",
                     "RequireIl2CppObject",
                     "RequireIl2CppMethod",
                     "RequireIl2CppPointer"
                 })
        {
            if (typeof(IL2CPP).GetMethod(
                    helperName,
                    BindingFlags.Static | BindingFlags.Public,
                    [typeof(IntPtr), typeof(string)]) is null)
            {
                throw new MissingMethodException(typeof(IL2CPP).FullName, helperName);
            }
        }

        var delegateType = typeof(Il2CppSystem.Delegate);
        foreach (var field in new[]
                 {
                     "method_ptr", "invoke_impl", "m_target",
                     "method", "method_code", "method_info"
                 })
        {
            var property = delegateType.GetProperty(field);
            if (property?.GetMethod == null || property.SetMethod == null)
                throw new MissingMemberException(delegateType.FullName, field);
        }

        var typeType = typeof(Il2CppSystem.Type);
        foreach (var propertyName in new[]
                 {
                     "_impl", "TypeHandle", "FullName", "IsByRef", "IsPrimitive"
                 })
        {
            if (typeType.GetProperty(propertyName)?.GetMethod == null)
                throw new MissingMemberException(typeType.FullName, propertyName);
        }
        if (typeType.GetMethod(
                "internal_from_handle",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
        {
            throw new MissingMethodException(typeType.FullName, "internal_from_handle");
        }
        if (typeType.GetMethod("GetMethod", [typeof(string)]) == null)
            throw new MissingMethodException(typeType.FullName, "GetMethod(String)");
        if (!typeType.GetMethods().Any(method =>
                method.Name == "MakeGenericType" && method.GetParameters().Length == 1))
        {
            throw new MissingMethodException(typeType.FullName, "MakeGenericType");
        }

        if (typeof(Il2CppSystem.RuntimeTypeHandle).GetField("value") == null)
            throw new MissingFieldException(typeof(Il2CppSystem.RuntimeTypeHandle).FullName, "value");
        if (typeof(Il2CppSystem.Reflection.MemberInfo).GetProperty("DeclaringType")?.GetMethod == null)
            throw new MissingMemberException(typeof(Il2CppSystem.Reflection.MemberInfo).FullName, "DeclaringType");
        if (typeof(Il2CppSystem.Reflection.MethodBase).GetMethod("GetParameters", Type.EmptyTypes) == null)
            throw new MissingMethodException(typeof(Il2CppSystem.Reflection.MethodBase).FullName, "GetParameters");
        if (typeof(Il2CppSystem.Reflection.MethodInfo).GetProperty("ReturnType")?.GetMethod == null)
            throw new MissingMemberException(typeof(Il2CppSystem.Reflection.MethodInfo).FullName, "ReturnType");
        if (typeof(Il2CppSystem.Reflection.ParameterInfo).GetProperty("ParameterType")?.GetMethod == null)
            throw new MissingMemberException(typeof(Il2CppSystem.Reflection.ParameterInfo).FullName, "ParameterType");
    }

    internal static bool TryGetProxyType(string assemblyName, string fullTypeName, out Type type)
    {
        lock (ProxyAssemblies)
        {
            if (ProxyAssemblies.TryGetValue(NormalizeAssemblyName(assemblyName), out var assembly) &&
                assembly.GetType(fullTypeName, false, false) is { } resolved)
            {
                type = resolved;
                return true;
            }
        }

        type = null!;
        return false;
    }

    internal static bool IsGeneratedProxyType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (ProxyAssemblies)
            return ProxyAssemblies.Values.Contains(type.Assembly);
    }

    internal static bool TryGetUniqueProxyType(
        string fullTypeName,
        out Type type,
        out string? error)
    {
        lock (ProxyAssemblies)
        {
            var matches = ProxyAssemblies.Values
                .Select(assembly => assembly.GetType(fullTypeName, false, false))
                .Where(candidate => candidate != null)
                .Distinct()
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                type = matches[0]!;
                error = null;
                return true;
            }

            type = null!;
            error = matches.Length == 0
                ? $"Generated proxy type is unavailable: {fullTypeName}"
                : $"Generated proxy type is ambiguous: {fullTypeName}";
            return false;
        }
    }

    private static void LoadAndValidateProxyAssemblies()
    {
        var proxyDirectory = Path.Combine(AppContext.BaseDirectory, "pc_compat_proxies");
        if (!Directory.Exists(proxyDirectory))
            throw new DirectoryNotFoundException($"PcCompat proxy directory is missing: {proxyDirectory}");

        lock (ProxyAssemblies)
        {
            ProxyAssemblies.Clear();
            var generatedCorlib = typeof(Il2CppSystem.Object).Assembly;
            ProxyAssemblies[NormalizeAssemblyName(generatedCorlib.GetName().Name ?? "Il2Cppmscorlib")] =
                generatedCorlib;

            var discovered = Directory.GetFiles(proxyDirectory, "*.dll")
                .Where(path => !Path.GetFileName(path).Equals(
                    "Il2Cppmscorlib.dll",
                    StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    path => Path.GetFileName(path),
                    path => path,
                    StringComparer.OrdinalIgnoreCase);
            var orderedFiles = PreferredProxyLoadOrder
                .Where(discovered.ContainsKey)
                .Concat(discovered.Keys
                    .Where(fileName => !PreferredProxyLoadOrder.Contains(
                        fileName,
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            foreach (var fileName in orderedFiles)
            {
                var path = Path.GetFullPath(discovered[fileName]);

                var expectedName = NormalizeAssemblyName(fileName);
                var actualName = AssemblyName.GetAssemblyName(path).Name ?? string.Empty;
                if (!actualName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    throw new BadImageFormatException(
                        $"PcCompat proxy identity mismatch: expected={expectedName}, actual={actualName}",
                        path);

                var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                    assembly => string.Equals(
                        assembly.GetName().Name,
                        expectedName,
                        StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var existingPath = string.IsNullOrWhiteSpace(existing.Location)
                        ? string.Empty
                        : Path.GetFullPath(existing.Location);
                    if (!existingPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Default ALC already contains a conflicting {expectedName}: {existingPath}");
                    ProxyAssemblies[expectedName] = existing;
                    continue;
                }

                ProxyAssemblies[expectedName] = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }

            foreach (var requiredFile in PreferredProxyLoadOrder)
            {
                var requiredName = NormalizeAssemblyName(requiredFile);
                if (!ProxyAssemblies.ContainsKey(requiredName))
                    throw new FileNotFoundException($"PcCompat proxy assembly is missing: {requiredFile}");
            }

            ValidateAllGenericProxyInitializers();
            ValidateAllNativePointerProducerGuards();

            ValidateType("Assembly-CSharp", "scrMarginTracker");
            ValidateType("Assembly-CSharp", "scrController");
            ValidateType("Assembly-CSharp", "scrPlayer");
            ValidateType("Assembly-CSharp", "scrPlanet");
            ValidateType("UnityEngine.CoreModule", "UnityEngine.Object");
            var gameObject = ValidateType("UnityEngine.CoreModule", "UnityEngine.GameObject");
            ValidateType("UnityEngine.CoreModule", "UnityEngine.Component");
            var behaviour = ValidateType("UnityEngine.CoreModule", "UnityEngine.Behaviour");
            var monoBehaviour = ValidateType("UnityEngine.CoreModule", "UnityEngine.MonoBehaviour");
            ValidateConstructor(gameObject, typeof(string));
            ValidateInstanceMethod(gameObject, "AddComponent", typeof(Il2CppSystem.Type));
            ValidateInstanceMethod(behaviour, "set_enabled", typeof(bool));
            ValidateConstructor(behaviour, typeof(IntPtr));
            ValidateConstructor(monoBehaviour, typeof(IntPtr));
            ValidateType("UnityEngine.CoreModule", "UnityEngine.AsyncOperation");
            ValidateType("UnityEngine.CoreModule", "UnityEngine.TextAsset");
            ValidateType("UnityEngine.TextRenderingModule", "UnityEngine.Font");
            ValidateType("UnityEngine.TextCoreFontEngineModule", "UnityEngine.TextCore.FaceInfo");
            ValidateType("UnityEngine.TextCoreFontEngineModule", "UnityEngine.TextCore.GlyphMetrics");
            ValidateType("UnityEngine.TextCoreFontEngineModule", "UnityEngine.TextCore.GlyphRect");
            ValidateType("UnityEngine.TextCoreFontEngineModule", "UnityEngine.TextCore.Glyph");
            ValidateType("UnityEngine.TextCoreTextEngineModule", "UnityEngine.TextCore.Text.FontAsset");
            ValidateType("UnityEngine.TextCoreTextEngineModule", "UnityEngine.TextCore.Text.Character");
            var assetBundle = ValidateType("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundle");
            ValidateType("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundleCreateRequest");
            ValidateType("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundleRequest");
            ValidateAbsentProxyMethod(assetBundle, "LoadFromFile");
            ValidateAbsentProxyMethod(assetBundle, "LoadAsset");
            ValidateAbsentProxyMethod(assetBundle, "LoadAllAssets");
            ValidateType("UnityEngine.IMGUIModule", "UnityEngine.GUILayout");
            ValidateType("UnityEngine.InputLegacyModule", "UnityEngine.Input");
            var canvasScaler = ValidateType("UnityEngine.UI", "UnityEngine.UI.CanvasScaler");
            var contentSizeFitter = ValidateType(
                "UnityEngine.UI",
                "UnityEngine.UI.ContentSizeFitter");
            var image = ValidateType("UnityEngine.UI", "UnityEngine.UI.Image");
            ValidateInstanceMethod(canvasScaler, "set_uiScaleMode", 1);
            ValidateInstanceMethod(contentSizeFitter, "set_horizontalFit", 1);
            ValidateInstanceMethod(contentSizeFitter, "set_verticalFit", 1);
            ValidateInstanceMethod(image, "set_type", 1);
            ValidateType("Unity.TextMeshPro", "TMPro.TextMeshProUGUI");
            ValidateType("Unity.TextMeshPro", "TMPro.TMP_FontAsset");
            ValidateType("Unity.TextMeshPro", "TMPro.TMP_Character");
            ValidateBlittableLayout(
                "UnityEngine.CoreModule",
                "UnityEngine.Vector2",
                8,
                ("x", 0),
                ("y", 4));
            ValidateBlittableLayout(
                "UnityEngine.CoreModule",
                "UnityEngine.Vector3",
                12,
                ("x", 0),
                ("y", 4),
                ("z", 8));
            ValidateBlittableLayout(
                "UnityEngine.CoreModule",
                "UnityEngine.Color",
                16,
                ("r", 0),
                ("g", 4),
                ("b", 8),
                ("a", 12));

            var tracker = ValidateType("Assembly-CSharp", "scrMarginTracker");
            ValidateReadableProperty(tracker, "hitMarginsCount");
            ValidateReadableProperty(tracker, "percentAcc", requireReadOnly: true);
            ValidateReadableProperty(tracker, "percentXAcc", requireReadOnly: true);
        }
    }

    private static Type ValidateType(string assemblyName, string fullTypeName)
    {
        if (!ProxyAssemblies.TryGetValue(NormalizeAssemblyName(assemblyName), out var assembly))
            throw new TypeLoadException($"PcCompat proxy assembly is not loaded: {assemblyName}");
        return assembly.GetType(fullTypeName, true, false)!;
    }

    private static void ValidateReadableProperty(Type type, string propertyName, bool requireReadOnly = false)
    {
        var property = type.GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetMethod is null)
            throw new MissingMemberException(type.FullName, propertyName);
        if (requireReadOnly && property.SetMethod is not null)
            throw new InvalidOperationException($"Proxy audit surface must remain read-only: {type.FullName}.{propertyName}");
    }

    private static void ValidateAbsentProxyMethod(Type type, string methodName)
    {
        var count = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Count(method => method.Name == methodName);
        if (count != 0)
        {
            throw new BadImageFormatException(
                $"Bridge-owned method leaked into Android native proxy surface: " +
                $"{type.FullName}.{methodName}; count={count}.");
        }
    }

    private static void ValidateInstanceMethod(Type type, string methodName, int parameterCount)
    {
        var matches = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Count(method =>
                method.Name == methodName &&
                !method.IsGenericMethodDefinition &&
                method.GetParameters().Length == parameterCount);
        if (matches != 1)
        {
            throw new MissingMethodException(
                $"Generated proxy method is missing or ambiguous: " +
                $"{type.FullName}.{methodName}/{parameterCount}; matches={matches}.");
        }
    }

    private static void ValidateInstanceMethod(
        Type type,
        string methodName,
        params Type[] parameters)
    {
        if (type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                parameters,
                modifiers: null) is null)
        {
            throw new MissingMethodException(
                type.FullName,
                $"{methodName}({string.Join(", ", parameters.Select(parameter => parameter.FullName))})");
        }
    }

    private static void ValidateConstructor(Type type, params Type[] parameters)
    {
        if (type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                parameters,
                modifiers: null) is null)
        {
            throw new MissingMethodException(
                type.FullName,
                $".ctor({string.Join(", ", parameters.Select(parameter => parameter.FullName))})");
        }
    }

    private static void ValidateGenericProxyInitializer(Type genericType)
    {
        var initializer = genericType.TypeInitializer
                          ?? throw new MissingMethodException(genericType.FullName, ".cctor");
        var calls = ReadMethodOperandNames(initializer);
        if (calls.Contains("il2cpp_class_get_type", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Generated generic proxy calls raw il2cpp_class_get_type: {genericType.FullName}.");
        }

        var isMethodStore = genericType.Name.StartsWith(
            "MethodInfoStoreGeneric_",
            StringComparison.Ordinal);
        var requiredHelpers = isMethodStore
            ? new[]
            {
                "GetIl2CppTypeForClass",
                "GetNativeClassPointerForGenericArgument",
                "RequireIl2CppObject",
                "RequireIl2CppMethod"
            }
            : new[]
            {
                "RequireIl2CppClass",
                "GetIl2CppTypeForClass",
                "GetNativeClassPointerForGenericArgument"
            };
        foreach (var required in requiredHelpers)
        {
            if (!calls.Contains(required, StringComparer.Ordinal))
            {
                throw new MissingMethodException(
                    $"Generated generic proxy initializer lacks {required}: {genericType.FullName}.");
            }
        }

        if (!isMethodStore && calls.Count(name => name == "RequireIl2CppClass") < 2)
        {
            throw new InvalidOperationException(
                $"Generated generic proxy does not guard the inflated class: {genericType.FullName}.");
        }
    }

    private static void ValidateAllGenericProxyInitializers()
    {
        foreach (var assembly in ProxyAssemblies.Values.Distinct())
        foreach (var type in assembly.GetTypes())
        {
            if (!type.ContainsGenericParameters)
                continue;

            ValidateGenericProxyInitializer(type);
        }
    }

    private static void ValidateAllNativePointerProducerGuards()
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var assembly in ProxyAssemblies.Values.Distinct())
        foreach (var type in assembly.GetTypes())
        {
            var methods = type.GetMethods(flags).Cast<MethodBase>()
                .Concat(type.GetConstructors(flags));
            if (type.TypeInitializer is { } initializer)
                methods = methods.Append(initializer);

            foreach (var method in methods)
            {
                if (method.GetMethodBody() is null)
                    continue;
                ValidateNativePointerProducerGuards(method, ReadMethodOperandNames(method));
            }
        }
    }

    private static void ValidateNativePointerProducerGuards(
        MethodBase method,
        IReadOnlyList<string> calls)
    {
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_object_new", "RequireIl2CppClass", "RequireIl2CppObject");
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_object_get_virtual_method", "RequireIl2CppMethod", "RequireIl2CppMethod");
        if (calls.Contains("il2cpp_object_get_virtual_method", StringComparer.Ordinal) &&
            !calls.Contains("Il2CppObjectBaseToPtrNotNull", StringComparer.Ordinal))
        {
            throw new BadImageFormatException(
                $"Generated proxy virtual dispatch accepts a null instance: {FormatMethodIdentity(method)}.");
        }
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_object_unbox", "RequireIl2CppObject", "RequireIl2CppPointer");
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_object_get_class", "RequireIl2CppObject", "RequireIl2CppClass");
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_value_box", "RequireIl2CppClass", "RequireIl2CppObject");
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_class_from_type", "RequireIl2CppPointer", "RequireIl2CppClass");
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_class_value_size", "RequireIl2CppClass", null);
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_class_is_valuetype", "RequireIl2CppClass", null);
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_method_get_from_reflection", "RequireIl2CppObject", "RequireIl2CppMethod");
        ValidateAdjacentPointerGuards(
            method, calls, "il2cpp_method_get_object", "RequireIl2CppClass", "RequireIl2CppObject");
        ValidateRequiredCallBeforeProducer(
            method, calls, "il2cpp_method_get_object", "RequireIl2CppMethod");
        ValidateRequiredCallBeforeProducer(
            method, calls, "il2cpp_runtime_invoke", "RequireIl2CppMethod");
    }

    private static void ValidateAdjacentPointerGuards(
        MethodBase method,
        IReadOnlyList<string> calls,
        string producer,
        string? requiredBefore,
        string? requiredAfter)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            if (!calls[index].Equals(producer, StringComparison.Ordinal))
                continue;

            if (requiredBefore is not null &&
                (index == 0 || !calls[index - 1].Equals(requiredBefore, StringComparison.Ordinal)))
            {
                throw new BadImageFormatException(
                    $"Generated proxy calls {producer} without preceding {requiredBefore}: " +
                    FormatMethodIdentity(method));
            }

            if (requiredAfter is not null &&
                (index + 1 >= calls.Count ||
                 !calls[index + 1].Equals(requiredAfter, StringComparison.Ordinal)))
            {
                throw new BadImageFormatException(
                    $"Generated proxy calls {producer} without following {requiredAfter}: " +
                    FormatMethodIdentity(method));
            }
        }
    }

    private static void ValidateRequiredCallBeforeProducer(
        MethodBase method,
        IReadOnlyList<string> calls,
        string producer,
        string requiredCall)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            if (!calls[index].Equals(producer, StringComparison.Ordinal))
                continue;
            if (calls.Take(index).Contains(requiredCall, StringComparer.Ordinal))
                continue;
            throw new BadImageFormatException(
                $"Generated proxy calls {producer} before {requiredCall}: {FormatMethodIdentity(method)}.");
        }
    }

    private static string FormatMethodIdentity(MethodBase method)
        => $"{method.Module.Assembly.GetName().Name}!{method.DeclaringType?.FullName}::{method.Name}";

    private static IReadOnlyList<string> ReadMethodOperandNames(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray()
                 ?? throw new MissingMethodException(method.DeclaringType?.FullName, method.Name);
        var result = new List<string>();
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            ushort value = first;
            if (first == 0xFE)
            {
                if (offset >= il.Length)
                    throw new BadImageFormatException("Truncated two-byte IL opcode.");
                value = (ushort)(0xFE00 | il[offset++]);
            }

            if (!IlOpCodes.TryGetValue(value, out var opCode))
                throw new BadImageFormatException($"Unknown IL opcode 0x{value:X4}.");

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
            if (operandOffset + operandSize > il.Length)
                throw new BadImageFormatException($"Truncated IL operand for {opCode.Name}.");

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(
                    il.AsSpan(operandOffset, sizeof(int)));
                try
                {
                    var resolved = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (resolved is not null)
                        result.Add(resolved.Name);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or BadImageFormatException)
                {
                    throw new BadImageFormatException(
                        $"Cannot resolve method token 0x{token:X8} in {method.DeclaringType?.FullName}.{method.Name}.",
                        exception);
                }
            }

            offset += operandSize;
        }

        return result;
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int operandOffset)
        => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => GetInlineSwitchSize(il, operandOffset),
            _ => throw new BadImageFormatException($"Unsupported IL operand type: {operandType}.")
        };

    private static int GetInlineSwitchSize(byte[] il, int operandOffset)
    {
        if (operandOffset + sizeof(int) > il.Length)
            throw new BadImageFormatException("Truncated IL switch operand.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, sizeof(int)));
        if (count < 0 || count > (il.Length - operandOffset - sizeof(int)) / sizeof(int))
            throw new BadImageFormatException("Invalid IL switch target count.");
        return sizeof(int) + count * sizeof(int);
    }

    private static readonly IReadOnlyDictionary<ushort, OpCode> IlOpCodes =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private static void ValidateBlittableLayout(
        string assemblyName,
        string fullTypeName,
        int expectedSize,
        params (string Name, int Offset)[] expectedFields)
    {
        var type = ValidateType(assemblyName, fullTypeName);
        if (!type.IsValueType)
            throw new TypeLoadException($"Generated proxy is not a value type: {fullTypeName}");

        var actualSize = Marshal.SizeOf(type);
        if (actualSize != expectedSize)
        {
            throw new BadImageFormatException(
                $"Generated proxy layout size mismatch: {fullTypeName} " +
                $"expected={expectedSize} actual={actualSize}");
        }

        foreach (var (fieldName, expectedOffset) in expectedFields)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException(fullTypeName, fieldName);
            if (field.FieldType != typeof(float))
            {
                throw new BadImageFormatException(
                    $"Generated proxy layout field type mismatch: " +
                    $"{fullTypeName}.{fieldName} expected=System.Single actual={field.FieldType.FullName}");
            }

            var actualOffset = Marshal.OffsetOf(type, fieldName).ToInt32();
            if (actualOffset != expectedOffset)
            {
                throw new BadImageFormatException(
                    $"Generated proxy layout field offset mismatch: " +
                    $"{fullTypeName}.{fieldName} expected={expectedOffset} actual={actualOffset}");
            }
        }
    }

    private static string NormalizeAssemblyName(string value)
    {
        var result = value.Trim();
        return result.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }

    private sealed class HookBrokerDetourProvider : IDetourProvider
    {
        public static readonly HookBrokerDetourProvider Instance = new();

        public IDetour Create<TDelegate>(nint original, TDelegate target) where TDelegate : Delegate
            => new HookBrokerDetour<TDelegate>(original, target);
    }

    private sealed class HookBrokerDetour<TDelegate> : IDetour
        where TDelegate : Delegate
    {
        private readonly TDelegate _targetDelegate;

        public HookBrokerDetour(nint target, TDelegate targetDelegate)
        {
            if (target == nint.Zero)
                throw new ArgumentException("Il2CppInterop detour target is null.", nameof(target));

            _targetDelegate = targetDelegate ??
                              throw new ArgumentNullException(nameof(targetDelegate));
            Target = target;
            Detour = Marshal.GetFunctionPointerForDelegate(_targetDelegate);
            var result = Dobby.Hook(
                Target,
                Detour,
                out var continuation,
                "Il2CppInterop.ClassInjector");
            if (result != 0 || continuation == nint.Zero)
            {
                throw new InvalidOperationException(
                    $"HookBroker rejected Il2CppInterop detour target=0x{Target:X} " +
                    $"detour=0x{Detour:X} result={result}.");
            }

            OriginalTrampoline = continuation;
        }

        public nint Target { get; }
        public nint Detour { get; }
        public nint OriginalTrampoline { get; }

        // HookBroker installs permanent layers immediately. ClassInjector's
        // staged Apply/Dispose contract therefore becomes idempotent/no-unhook.
        public void Apply() { }

        public T GenerateTrampoline<T>() where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(OriginalTrampoline);

        public void Dispose() { }
    }
}

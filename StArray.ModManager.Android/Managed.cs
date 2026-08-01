using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Android.PcCompat;
using StArray.ModManager.Android.UI;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Manager;
using StArray.ModManager.Resources;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android;

/// <summary>CoreCLR entry / 加载器入口 — called by native delegate</summary>
/// <summary>CoreCLR 入口 / 加载器入口 — called by native delegate</summary>
public static class Managed
{
    /// <summary>当前管理器 DLL 的完整路径</summary>
    public static string AssemblyPath = string.Empty;
    private static StreamWriter? _logWriter;
    private static readonly object _logLock = new();
    private static readonly object s_nativeLibraryLock = new();
    private static bool s_nativeResolverInstalled;
    private static IntPtr s_il2CppHandle;
    private static ModManagerUI? s_ui;
    private static ModLoader? s_loader;
    private static AndroidModManagerPlatformServices? s_platformServices;
    private static Timer? s_backgroundLoadPollTimer;
    private static int s_backgroundLoadScheduleWarning;
    private static Timer? s_imguiInstallPollTimer;
    private static int s_imguiInstallAttempted;
    private static int s_imguiInstalled;
    private static int s_imguiInstallFailed;
    /// <summary>原生入口，初始化 Logger 桥接、扫描 Mod、启动 ImGui</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Entry(int argc, IntPtr argv)
    {
        try
        {
            return EntryCore(argc, argv);
        }
        catch (Exception ex)
        {
            try
            {
                AndroidUtils.Error(nameof(Managed), $"Unhandled managed entry exception: {ex}");
            }
            catch
            {
                // Nothing managed may escape the unmanaged CoreCLR entry point.
            }
            return -1;
        }
    }

    private static int EntryCore(int argc, IntPtr argv)
    {
        bool benchmarkEnabled = EnvEnabled("STARRAY_MODMANAGER_BENCHMARK", false);
        bool fileLogEnabled = EnvEnabled("STARRAY_MODMANAGER_ENABLE_FILE_LOG", false);
        bool uiEnabled = EnvEnabled("STARRAY_MODMANAGER_ENABLE_UI", false);
        var totalSw = benchmarkEnabled ? Stopwatch.StartNew() : null;

        // 桥接 Logger → Android logcat
        Logger.OnLog += (level, tag, msg) =>
        {
            var prio = level switch
            {
                Logger.Level.Debug => AndroidUtils.Priority.Debug,
                Logger.Level.Info  => AndroidUtils.Priority.Info,
                Logger.Level.Warn  => AndroidUtils.Priority.Warn,
                Logger.Level.Error => AndroidUtils.Priority.Error,
                _                  => AndroidUtils.Priority.Info
            };
            try
            {
                AndroidUtils.Write(prio, tag, msg);
            }
            catch
            {
                // Logging must never abort startup or native callbacks.
            }
        };
        LogBenchmark(benchmarkEnabled, "Logger bridge", null);

        InstallNativeLibraryResolvers();
        LogBenchmark(benchmarkEnabled, "Native library resolvers", null);

        bool pcCompatEnabled = true;

        // Android linker namespaces can make RTLD_NOLOAD return null even when
        // libil2cpp.so is already mapped. Use the verified native handle as
        // the authoritative runtime-presence signal for Android MODs.
        RuntimeBackend runtimeBackend;
        if (OperatingSystem.IsAndroid() && Il2CppNativeBridge.GetHandle() != IntPtr.Zero)
        {
            RuntimeManager.SetBackend(RuntimeBackend.Il2Cpp);
            runtimeBackend = RuntimeBackend.Il2Cpp;
        }
        else
        {
            runtimeBackend = RuntimeManager.Detect();
        }
        Logger.Info(nameof(Managed), $"native managed runtime={runtimeBackend}");

        HookHelper.Instance ??= new DobbyHook();
        if (pcCompatEnabled)
        {
            if (!PcCompatIl2CppInteropBootstrap.TryStart())
            {
                pcCompatEnabled = false;
                Logger.Error(
                    nameof(Managed),
                    "PC MOD compatibility disabled because Il2CppInterop initialization failed");
            }
            else
            {
                PcCompatManagedComponentOwnerHost.Install();
                PcCompatAndroidManagedAssemblyRewrite.Install();
                PcCompatAndroidResourceAssemblyCompile.Install();
                PcCompatAndroidTargetSignature.Install();
                LogBenchmark(benchmarkEnabled, "Il2CppInterop runtime", null);

                PcCompatDobbyBridge.Install();
                PcCompatManagedSelfRenderBridge.Install();
                PcCompatUnityHudBridge.Install();
                PcCompatKeyViewerFallbackBridge.Install();
                PcCompatResourceBundleLoader.Install();
                PcCompatInjectedOnGUIHost.Install();
                PcCompatDiagnosticsExportBridge.Install();
                LogBenchmark(benchmarkEnabled, "PcCompat Dobby bridge", null);
            }
        }

        // 解析命令行参数
        var sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
        string[] args = argv != IntPtr.Zero && argc > 0 ? new string[argc] : [];
        for (int i = 0; i < args.Length; i++)
        {
            IntPtr pStr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
            args[i] = pStr != IntPtr.Zero ? Marshal.PtrToStringUTF8(pStr) ?? string.Empty : string.Empty;
        }
        LogBenchmark(benchmarkEnabled, $"Args parse ({argc} args)", sw);

        // 路径解析
        sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
        string baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = AndroidUtils.GetInternalFilesDir() ?? Environment.CurrentDirectory;

        string modsPath = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : Path.Combine(baseDir, "mods");
        string configDir = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            ? args[1]
            : Path.Combine(baseDir, "config");
        AssemblyPath = !string.IsNullOrWhiteSpace(typeof(Managed).Assembly.Location)
            ? typeof(Managed).Assembly.Location
            : baseDir;

        Directory.CreateDirectory(modsPath);
        Directory.CreateDirectory(configDir);
        Logger.Info(nameof(Managed), $"mods={modsPath}");
        Logger.Info(nameof(Managed), $"config={configDir}");
        LogBenchmark(benchmarkEnabled, "Path resolve", sw);

        sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
        if (pcCompatEnabled)
            PcCompatCapabilityBundleRegistry.Install();
        LogBenchmark(benchmarkEnabled, "PcCompat capability registry", sw);

        if (fileLogEnabled)
        {
            try
            {
                var rootDir = Path.GetDirectoryName(modsPath);
                if (string.IsNullOrWhiteSpace(rootDir))
                    rootDir = configDir;
                Directory.CreateDirectory(rootDir);
                _logWriter = new StreamWriter(Path.Combine(rootDir, "manager.log"), append: true) { AutoFlush = true };
                Logger.OnLog += (level, tag, msg) =>
                {
                    lock (_logLock)
                        _logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{tag}] {msg}");
                };
            }
            catch (Exception ex)
            {
                Logger.Warn(nameof(Managed), $"file log disabled: {ex.Message}");
            }
        }

        // 初始化
        sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
        var loader = new ModLoader(modsPath);
        s_loader = loader;
        if (pcCompatEnabled)
            loader.SetPendingLoadCompletionScheduler(
                PcCompatResourceBundleLoader.TryScheduleUnityMainWork);
        LogBenchmark(benchmarkEnabled, "ModLoader init", sw);

        sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
        var startupConfig = ModManagerConfig.Load(configDir);
        L10n.SetLanguage(startupConfig.Language);
        if (pcCompatEnabled)
        {
            PcCompatTouchLaneMappingRuntime.RegisterNativeSink(mode =>
            {
                if (!PcCompatNativeHookRules.TrySetTouchLaneMappingMode(mode))
                {
                    Logger.Warn(
                        nameof(Managed),
                        "native touch KeyViewer mapping mode was not applied: " + mode);
                }
            });
            PcCompatTouchLaneMappingRuntime.RegisterNativeReuseDelaySink(milliseconds =>
            {
                if (!PcCompatNativeHookRules.TrySetTouchContactReuseDelayMilliseconds(milliseconds))
                {
                    Logger.Warn(
                        nameof(Managed),
                        "native touch KeyViewer contact reuse delay was not applied: " +
                        milliseconds);
                }
            });
            PcCompatTouchLaneMappingRuntime.SetTouchContactReuseDelayMilliseconds(
                startupConfig.TouchKeyViewerContactReuseDelayMilliseconds);
            PcCompatTouchLaneMappingRuntime.SetMode(startupConfig.TouchKeyViewerMappingMode);
        }
        if (string.IsNullOrEmpty(startupConfig.ModsDirectory))
            startupConfig.ModsDirectory = loader.ModsDirectory;
        loader.ScanMods();
        var autoStarted = loader.LoadConfiguredEnabledMods(startupConfig.ModEnabled);
        Logger.Info(
            nameof(Managed),
            $"startup auto-load scanned={loader.Mods.Count} configured={startupConfig.ModEnabled.Count} started={autoStarted}");
        StartBackgroundLoadPoller(loader);
        LogBenchmark(benchmarkEnabled, "Startup mod scan/autoload", sw);

        if (uiEnabled)
        {
            sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
            var platform = new AndroidModManagerPlatformServices();
            var ui = new ModManagerUI(loader, configDir, platform);
            s_platformServices = platform;
            s_ui = ui;
            LogBenchmark(benchmarkEnabled, "ModManagerUI init", sw);

            if (EnvEnabled("STARRAY_MODMANAGER_EAGER_IMGUI", false))
                TryInstallImGuiRenderer(ui, benchmarkEnabled);
            else
                StartLazyImGuiInstallPoller(ui);
        }
        else
        {
            Logger.Info(nameof(Managed), "UI disabled by STARRAY_MODMANAGER_ENABLE_UI");
        }

        if (totalSw != null)
        {
            totalSw.Stop();
            Logger.Error($"{nameof(Managed)}-Benchmark", $"=== Startup total: {totalSw.Elapsed.TotalSeconds:F3}s ===");
        }
        return 0;
    }

    private static void StartBackgroundLoadPoller(ModLoader loader)
    {
        s_backgroundLoadPollTimer ??= new Timer(
            _ =>
            {
                if (!loader.HasPendingAsyncLoads)
                    return;

                try
                {
                    if (loader.RequestPendingLoadUpdate())
                    {
                        Volatile.Write(ref s_backgroundLoadScheduleWarning, 0);
                    }
                    else if (Interlocked.Exchange(ref s_backgroundLoadScheduleWarning, 1) == 0)
                    {
                        Logger.Warn(nameof(Managed), "waiting for UnityMain MOD finalization hook");
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref s_backgroundLoadScheduleWarning, 1) == 0)
                        Logger.Warn(nameof(Managed), $"UnityMain MOD finalization request failed: {ex.Message}");
                }
            },
            null,
            dueTime: 100,
            period: 100);
    }

    private static void StartLazyImGuiInstallPoller(ModManagerUI ui)
    {
        s_imguiInstallPollTimer ??= new Timer(
            _ =>
            {
                if (Volatile.Read(ref s_imguiInstalled) != 0 ||
                    Volatile.Read(ref s_imguiInstallFailed) != 0)
                    return;

                bool shouldInstall;
                try
                {
                    shouldInstall = ui.IsOverlayVisible || ui.RequiresRenderingWhenHidden;
                }
                catch (Exception ex)
                {
                    Logger.Warn(nameof(Managed), $"lazy ImGui predicate failed: {ex.Message}");
                    return;
                }

                if (shouldInstall)
                    TryInstallImGuiRenderer(ui, benchmarkEnabled: false);
            },
            null,
            dueTime: 50,
            period: 50);
    }

    private static void TryInstallImGuiRenderer(ModManagerUI ui, bool benchmarkEnabled)
    {
        if (Volatile.Read(ref s_imguiInstalled) != 0)
            return;
        if (Volatile.Read(ref s_imguiInstallFailed) != 0)
            return;
        if (Interlocked.Exchange(ref s_imguiInstallAttempted, 1) != 0)
            return;

        var sw = benchmarkEnabled ? Stopwatch.StartNew() : null;
        try
        {
            ImGuiEGLRender.OnRender += ui.Render;
            ImGuiEGLRender.SetRenderWhenHiddenPredicate(() => ui.RequiresRenderingWhenHidden);
            if (ImGuiEGLRender.Install())
            {
                Volatile.Write(ref s_imguiInstalled, 1);
                s_imguiInstallPollTimer?.Dispose();
                s_imguiInstallPollTimer = null;
                LogBenchmark(benchmarkEnabled, "ImGui install", sw);
                Logger.Info(nameof(Managed), "ImGui renderer installed lazily");
            }
            else
            {
                ImGuiEGLRender.OnRender -= ui.Render;
                ImGuiEGLRender.SetRenderWhenHiddenPredicate(null);
                Volatile.Write(ref s_imguiInstallFailed, 1);
                s_imguiInstallPollTimer?.Dispose();
                s_imguiInstallPollTimer = null;
                Logger.Warn(nameof(Managed), "ImGui install returned false");
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            ImGuiEGLRender.OnRender -= ui.Render;
            ImGuiEGLRender.SetRenderWhenHiddenPredicate(null);
            Volatile.Write(ref s_imguiInstallFailed, 1);
            s_imguiInstallPollTimer?.Dispose();
            s_imguiInstallPollTimer = null;
            Logger.Warn(nameof(Managed), $"ImGui backend missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            ImGuiEGLRender.OnRender -= ui.Render;
            ImGuiEGLRender.SetRenderWhenHiddenPredicate(null);
            Volatile.Write(ref s_imguiInstallFailed, 1);
            s_imguiInstallPollTimer?.Dispose();
            s_imguiInstallPollTimer = null;
            Logger.Error(nameof(Managed), $"ImGui install failed: {ex}");
        }
        finally
        {
            if (Volatile.Read(ref s_imguiInstalled) == 0)
                Volatile.Write(ref s_imguiInstallAttempted, 0);
        }
    }

    private static bool EnvEnabled(string name, bool defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogBenchmark(bool enabled, string label, Stopwatch? sw)
    {
        if (!enabled)
            return;
        double seconds = sw?.Elapsed.TotalSeconds ?? 0;
        Logger.Error($"{nameof(Managed)}-Benchmark", $"{label}: {seconds:F3}s");
    }

    private static void InstallNativeLibraryResolvers()
    {
        if (s_nativeResolverInstalled)
            return;

        s_nativeResolverInstalled = true;
        TrySetResolver(typeof(ImGui).Assembly);
        TrySetResolver(typeof(Managed).Assembly);
        TrySetResolver(typeof(Il2CppFunctions).Assembly);
    }

    private static void TrySetResolver(Assembly assembly)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // Resolver already installed for this assembly.
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(Managed), $"Native resolver install failed for {assembly.GetName().Name}: {ex.Message}");
        }
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName.Equals("IL2CPP_LIBRARY_NAME", StringComparison.OrdinalIgnoreCase) ||
            libraryName.Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase) ||
            libraryName.Equals("GameAssembly", StringComparison.OrdinalIgnoreCase))
            return ResolveIl2CppLibrary();

        if (!libraryName.Equals("cimgui", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        if (NativeLibrary.TryLoad("starray_modmanager", assembly, searchPath, out var handle))
            return handle;
        if (NativeLibrary.TryLoad("libstarray_modmanager.so", assembly, searchPath, out handle))
            return handle;

        Logger.Error(nameof(Managed), "Failed to resolve cimgui from starray_modmanager");
        return IntPtr.Zero;
    }

    private static IntPtr ResolveIl2CppLibrary()
    {
        lock (s_nativeLibraryLock)
        {
            if (s_il2CppHandle != IntPtr.Zero)
                return s_il2CppHandle;

            s_il2CppHandle = Il2CppNativeBridge.GetHandle();
            if (s_il2CppHandle == IntPtr.Zero)
                Logger.Error(nameof(Managed), "Failed to resolve libil2cpp.so through native namespace bridge");
            return s_il2CppHandle;
        }
    }
}

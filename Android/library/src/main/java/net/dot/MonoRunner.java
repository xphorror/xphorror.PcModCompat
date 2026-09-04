// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package net.dot;

import android.util.Log;
import java.io.File;
import java.util.ArrayList;

/**
 * MonoRunner — CoreCLR 启动器。
 * <pre>
 *   MonoRunner.dotnetRoot("/sdcard/ModManager/runtime")
 *       .addAssemblyDir("/sdcard/ModManager/loader")
 *       .run("ModManager.dll", "StArray.ModManager.Mono", "Entry");
 * </pre>
 */
public final class MonoRunner {

    private static final String TAG = "StArray.MonoRunner";
    private static String s_dotnetRoot;
    private static final ArrayList<String> s_assemblyDirs = new ArrayList<>();
    private static final ArrayList<String> s_nativeDirs = new ArrayList<>();
    private static boolean s_initialized;

    static {
        System.loadLibrary("starray_modmanager");
    }

    // ========================================================================
    // Public API
    // ========================================================================

    public static MonoRunner dotnetRoot(String path)  { s_dotnetRoot = path; return null; }

    public static MonoRunner addAssemblyDir(String dir) {
        if (!s_assemblyDirs.contains(dir)) s_assemblyDirs.add(dir); return null;
    }

    public static MonoRunner addNativeDir(String dir) {
        if (!s_nativeDirs.contains(dir)) s_nativeDirs.add(dir); return null;
    }

    /**
     * 启动 CoreCLR + delegate 调用入口方法（无参）。
     * @param entryDll   入口 DLL 文件名
     * @param typeName   类型全名
     * @param methodName 方法名
     * @return 托管退出码
     */
    public static int run(String entryDll, String typeName, String methodName) {
        return run(entryDll, typeName, methodName, new String[0]);
    }

    /**
     * 启动 CoreCLR + delegate 调用入口方法（带参）。
     * 托管入口应声明为: static int Entry(int argc, IntPtr argv)
     * 参见 <a href="https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting">NET 托管文档</a>
     * @param entryDll   入口 DLL 文件名
     * @param typeName   类型全名
     * @param methodName 方法名
     * @param args       传给入口方法的参数
     * @return 托管退出码
     */
    public static int run(String entryDll, String typeName, String methodName, String... args) {
        final long startedAt = android.os.SystemClock.elapsedRealtime();
        Log.i(TAG, "[DEBUG-coreclr-init-v2] run begin root=" + s_dotnetRoot
                + " entry=" + entryDll + " type=" + typeName + " method=" + methodName
                + " initialized=" + s_initialized + " args=" + (args == null ? -1 : args.length));
        if (s_dotnetRoot == null || s_dotnetRoot.isEmpty()) {
            Log.e(TAG, "[DEBUG-coreclr-init-v2] run rejected: dotnetRoot is empty");
            throw new IllegalStateException("dotnetRoot not set");
        }
        if (entryDll == null || entryDll.isEmpty() || typeName == null || typeName.isEmpty()
                || methodName == null || methodName.isEmpty()) {
            Log.e(TAG, "[DEBUG-coreclr-init-v2] run rejected: entry/type/method is empty");
            throw new IllegalArgumentException("CoreCLR entry arguments must not be empty");
        }

        Log.i(TAG, "[DEBUG-coreclr-init-v2] inputs root=" + s_dotnetRoot
                + " entry=" + entryDll
                + " args=" + java.util.Arrays.toString(args)
                + " dirs=" + s_assemblyDirs
                + " native=" + s_nativeDirs);
        File root = new File(s_dotnetRoot);
        File entry = new File(root, entryDll);
        Log.i(TAG, "[DEBUG-coreclr-init-v2] path rootExists=" + root.exists()
                + " rootDir=" + root.isDirectory() + " rootReadable=" + root.canRead()
                + " entry=" + entry.getAbsolutePath() + " entryExists=" + entry.exists()
                + " entryReadable=" + entry.canRead() + " entryBytes="
                + (entry.exists() ? entry.length() : -1));

        // 环境变量
        int dotnetRootEnv = setEnv("DOTNET_ROOT", s_dotnetRoot);
        int telemetryEnv = setEnv("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        int homeEnv = setEnv("HOME", s_dotnetRoot);
        Log.i(TAG, "[DEBUG-coreclr-init-v2] env basic dotnetRoot=" + dotnetRootEnv
                + " telemetry=" + telemetryEnv + " home=" + homeEnv);

        // 托管搜索路径（dotnetRoot 排最前）
        s_assemblyDirs.add(0, s_dotnetRoot);
        String tpa = join(s_assemblyDirs, ":");
        int tpaEnv = setEnv("TRUSTED_PLATFORM_ASSEMBLIES", tpa);
        int appPathsEnv = setEnv("APP_PATHS", tpa);

        // 原生搜索路径
        int nativePathsEnv = 0;
        if (!s_nativeDirs.isEmpty())
            nativePathsEnv = setEnv("NATIVE_DLL_SEARCH_DIRECTORIES", join(s_nativeDirs, ":"));
        Log.i(TAG, "[DEBUG-coreclr-init-v2] env paths tpaResult=" + tpaEnv
                + " appPathsResult=" + appPathsEnv + " nativePathsResult=" + nativePathsEnv
                + " tpaCount=" + s_assemblyDirs.size() + " nativeCount=" + s_nativeDirs.size()
                + " tpaChars=" + tpa.length());

        // 初始化
        Log.i(TAG, "[DEBUG-coreclr-init-v2] initRuntime begin elapsedMs="
                + (android.os.SystemClock.elapsedRealtime() - startedAt));
        int rv = initRuntime(s_dotnetRoot, entryDll, 0);
        Log.i(TAG, "[DEBUG-coreclr-init-v2] initRuntime end rv=0x"
                + Integer.toHexString(rv) + " initialized=" + s_initialized
                + " elapsedMs=" + (android.os.SystemClock.elapsedRealtime() - startedAt));
        if (rv != 0) {
            Log.e(TAG, "[DEBUG-coreclr-init-v2] CoreCLR initialization failed rv=0x"
                    + Integer.toHexString(rv));
            throw new RuntimeException("coreclr_initialize: 0x" + Integer.toHexString(rv));
        }
        s_initialized = true;
        Log.i(TAG, "[DEBUG-coreclr-init-v2] CoreCLR initialized elapsedMs="
                + (android.os.SystemClock.elapsedRealtime() - startedAt));

        // delegate 调用（传参）
        if (args.length > 0) {
            int result = execEntryPointWithArgs(entryDll, entryDll, typeName, methodName, args);
            Log.i(TAG, "[DEBUG-coreclr-init-v2] entry end rv=" + result + " elapsedMs="
                    + (android.os.SystemClock.elapsedRealtime() - startedAt));
            return result;
        } else {
            int result = execEntryPoint(entryDll, entryDll, typeName, methodName);
            Log.i(TAG, "[DEBUG-coreclr-init-v2] entry end rv=" + result + " elapsedMs="
                    + (android.os.SystemClock.elapsedRealtime() - startedAt));
            return result;
        }
    }

    public static void stop() {
        if (!s_initialized) return;
        freeNativeResources();
        s_initialized = false;
    }

    // ========================================================================
    // Native
    // ========================================================================
    public static native int setEnv(String key, String value);
    public static native int initRuntime(String libsDir, String entryPointLibName, int localDateTimeOffset);
    public static native int execEntryPoint(String entryPointLibName,
        String assemblyName, String typeName, String methodName);
    public static native int execEntryPointWithArgs(String entryPointLibName,
        String assemblyName, String typeName, String methodName, String[] args);
    public static native void freeNativeResources();

    private static String join(ArrayList<String> list, String sep) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < list.size(); i++) {
            if (i > 0) sb.append(sep);
            sb.append(list.get(i));
        }
        return sb.toString();
    }
}

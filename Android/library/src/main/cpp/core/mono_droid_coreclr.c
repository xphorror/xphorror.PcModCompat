// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <sys/stat.h>
#include <stdlib.h>
#include <stdio.h>
#include <fcntl.h>
#include <errno.h>
#include <string.h>
#include <jni.h>
#include <android/log.h>
#include <sys/system_properties.h>
#include <sys/mman.h>
#include <assert.h>
#include <unistd.h>
#include <dlfcn.h>
#include <coreclrhost.h>
#include <dirent.h>
#include <inttypes.h>
#include <pthread.h>
#include <stdint.h>
#include "runtime_il2cpp_bridge.h"

#include <corehost/host_runtime_contract.h>

void modmanager_pccompat_start_hook_coordinator(void);

/********* exported symbols *********/

/* JNI exports */

jint
Java_net_dot_MonoRunner_setEnv (JNIEnv* env, jclass thiz, jstring j_key, jstring j_value);

int
Java_net_dot_MonoRunner_initRuntime (JNIEnv* env, jclass thiz, jstring j_files_dir, jstring j_entryPointLibName,
                                     jint current_local_time);

int
Java_net_dot_MonoRunner_execEntryPoint (JNIEnv* env, jclass thiz, jstring j_entryPointLibName, jstring j_assemblyName, jstring j_typeName, jstring j_methodName);

int
Java_net_dot_MonoRunner_execEntryPointWithArgs (JNIEnv* env, jclass thiz, jstring j_entryPointLibName, jstring j_assemblyName,
    jstring j_typeName, jstring j_methodName, jobjectArray j_args);

void
Java_net_dot_MonoRunner_freeNativeResources (JNIEnv* env, jclass thiz);

/********* implementation *********/

static char* g_bundle_path = NULL;
static const char* g_executable_path = NULL;
static unsigned int g_coreclr_domainId = 0;
static void* g_coreclr_handle = NULL;
static void* g_coreclr_library = NULL;
static int g_coreclr_init_attempted = 0;
static int g_coreclr_init_result = 0;
static coreclr_initialize_ptr g_coreclr_initialize = NULL;
static coreclr_shutdown_ptr g_coreclr_shutdown = NULL;
static coreclr_create_delegate_ptr g_coreclr_create_delegate = NULL;
static coreclr_execute_assembly_ptr g_coreclr_execute_assembly = NULL;
static JavaVM* g_java_vm = NULL;
static void* g_android_crypto_library = NULL;

#define MAX_MAPPED_COUNT 256
static void* g_mapped_files[MAX_MAPPED_COUNT];
static size_t g_mapped_file_sizes[MAX_MAPPED_COUNT];
static unsigned int g_mapped_files_count = 0;

static struct host_runtime_contract g_host_contract = {
    .size = sizeof(struct host_runtime_contract)
};

#define LOG_INFO(fmt, ...) __android_log_print(ANDROID_LOG_INFO, "DOTNET", fmt, ##__VA_ARGS__)
#define LOG_ERROR(fmt, ...) __android_log_print(ANDROID_LOG_ERROR, "DOTNET", fmt, ##__VA_ARGS__)

#if defined(__arm__)
#define ANDROID_RUNTIME_IDENTIFIER "android-arm"
#elif defined(__aarch64__)
#define ANDROID_RUNTIME_IDENTIFIER "android-arm64"
#elif defined(__i386__)
#define ANDROID_RUNTIME_IDENTIFIER "android-x86"
#elif defined(__x86_64__)
#define ANDROID_RUNTIME_IDENTIFIER "android-x64"
#else
#error Unknown architecture
#endif

static void
strncpy_str (JNIEnv *env, char *buff, jstring str, int nbuff)
{
    jboolean isCopy = 0;
    const char *copy_buff = (*env)->GetStringUTFChars (env, str, &isCopy);
    strncpy (buff, copy_buff, nbuff);
    buff[nbuff - 1] = '\0'; // ensure '\0' terminated
    if (isCopy)
        (*env)->ReleaseStringUTFChars (env, str, copy_buff);
}

static int
bundle_executable_path (const char* executable, const char* bundle_path, const char** executable_path)
{
    size_t executable_path_len = strlen(bundle_path) + strlen(executable) + 1; // +1 for '/'
    char* temp_path = (char*)malloc(sizeof(char) * (executable_path_len + 1)); // +1 for '\0'
    if (temp_path == NULL)
    {
        return -1;
    }

    size_t res = snprintf(temp_path, (executable_path_len + 1), "%s/%s", bundle_path, executable);
    if (res < 0 || res != executable_path_len)
    {
        return -1;
    }
    *executable_path = temp_path;
    return (int)executable_path_len;
}

static void*
load_runtime_library_optional(const char* bundle_path, const char* soname)
{
    char full_path[2048];
    int written = snprintf(full_path, sizeof(full_path), "%s/%s", bundle_path, soname);
    if (written <= 0 || written >= (int)sizeof(full_path))
    {
        LOG_ERROR("Runtime library path too long: %s", soname);
        return NULL;
    }

    void* handle = dlopen(full_path, RTLD_NOW | RTLD_GLOBAL);
    if (handle == NULL)
    {
        LOG_INFO("Runtime library not loaded: %s (%s)", soname, dlerror());
    }
    return handle;
}

static int
initialize_runtime_jni_library(void* handle, const char* soname)
{
    if (handle == NULL)
    {
        LOG_ERROR("Required JNI runtime library was not loaded: %s", soname);
        return -1;
    }

    if (g_java_vm == NULL)
    {
        LOG_ERROR("Cannot initialize JNI runtime library %s: JavaVM is unavailable", soname);
        return -1;
    }

    dlerror();
    typedef jint (*jni_on_load_fn)(JavaVM*, void*);
    jni_on_load_fn on_load = (jni_on_load_fn)dlsym(handle, "JNI_OnLoad");
    const char* error = dlerror();
    if (error != NULL || on_load == NULL)
    {
        LOG_ERROR("JNI runtime library %s has no JNI_OnLoad: %s", soname, error ? error : "null");
        return -1;
    }

    jint version = on_load(g_java_vm, NULL);
    if (version == JNI_ERR)
    {
        LOG_ERROR("JNI_OnLoad failed for runtime library %s", soname);
        return -1;
    }

    LOG_INFO("JNI runtime library initialized: %s version=0x%x", soname, version);
    return 0;
}

static void*
resolve_coreclr_symbol(const char* name)
{
    dlerror();
    void* symbol = dlsym(g_coreclr_library, name);
    const char* error = dlerror();
    if (error != NULL || symbol == NULL)
    {
        LOG_ERROR("Failed to resolve CoreCLR symbol %s: %s", name, error ? error : "null");
        return NULL;
    }
    return symbol;
}

static int
load_coreclr_runtime(const char* bundle_path)
{
    if (g_coreclr_library != NULL)
        return 0;

    load_runtime_library_optional(bundle_path, "libSystem.Native.so");
    load_runtime_library_optional(bundle_path, "libSystem.Globalization.Native.so");
    load_runtime_library_optional(bundle_path, "libSystem.IO.Compression.Native.so");
    g_android_crypto_library = load_runtime_library_optional(
        bundle_path,
        "libSystem.Security.Cryptography.Native.Android.so");
    if (initialize_runtime_jni_library(
            g_android_crypto_library,
            "libSystem.Security.Cryptography.Native.Android.so") != 0)
        return -1;
    load_runtime_library_optional(bundle_path, "libclrjit.so");

    g_coreclr_library = load_runtime_library_optional(bundle_path, "libcoreclr.so");
    if (g_coreclr_library == NULL)
    {
        LOG_ERROR("Required runtime library libcoreclr.so was not loaded");
        return -1;
    }

    g_coreclr_initialize =
        (coreclr_initialize_ptr)resolve_coreclr_symbol("coreclr_initialize");
    g_coreclr_shutdown =
        (coreclr_shutdown_ptr)resolve_coreclr_symbol("coreclr_shutdown");
    g_coreclr_create_delegate =
        (coreclr_create_delegate_ptr)resolve_coreclr_symbol("coreclr_create_delegate");
    g_coreclr_execute_assembly =
        (coreclr_execute_assembly_ptr)resolve_coreclr_symbol("coreclr_execute_assembly");

    if (g_coreclr_initialize == NULL ||
        g_coreclr_shutdown == NULL ||
        g_coreclr_create_delegate == NULL ||
        g_coreclr_execute_assembly == NULL)
    {
        dlclose(g_coreclr_library);
        g_coreclr_library = NULL;
        return -1;
    }

    return 0;
}

static bool
try_map_assembly(const char* dir, const char* name, void** data, int64_t* size)
{
    char full_path[1024];
    size_t path_len = strlen(dir) + strlen(name) + 1;
    size_t res = snprintf(full_path, path_len + 1, "%s/%s", dir, name);
    if (res < 0 || res != path_len) return false;

    int fd = open(full_path, O_RDONLY);
    if (fd == -1) return false;

    struct stat buf;
    if (fstat(fd, &buf) == -1) { close(fd); return false; }

    int64_t size_local = buf.st_size;
    void* mapped = mmap(NULL, size_local, PROT_READ, MAP_PRIVATE, fd, 0);
    if (mapped == MAP_FAILED) { close(fd); return false; }

    g_mapped_files[g_mapped_files_count] = mapped;
    g_mapped_file_sizes[g_mapped_files_count] = size_local;
    g_mapped_files_count++;
    close(fd);
    *data = mapped;
    *size = size_local;
    LOG_INFO("Mapped %s -> %s", name, full_path);
    return true;
}

static bool
external_assembly_probe(const char* relative_assembly_path, void** data, int64_t* size)
{
    if (g_mapped_files_count >= MAX_MAPPED_COUNT)
    {
        LOG_ERROR("Too many mapped files, cannot map %s", relative_assembly_path);
        return false;
    }

    // 1) Try g_bundle_path (dotnet root)
    if (try_map_assembly(g_bundle_path, relative_assembly_path, data, size))
        return true;

    // 2) Try each APP_PATHS directory
    const char* app_paths = getenv("APP_PATHS");
    if (app_paths) {
        char* paths = strdup(app_paths);
        char* tok = strtok(paths, ":");
        while (tok) {
            if (strcmp(tok, g_bundle_path) != 0 && // skip g_bundle_path (already tried)
                try_map_assembly(tok, relative_assembly_path, data, size)) {
                free(paths);
                return true;
            }
            tok = strtok(NULL, ":");
        }
        free(paths);
    }

    return false;
}

static void
free_resources ()
{
    if (g_bundle_path)
    {
        free (g_bundle_path);
        g_bundle_path = NULL;
    }
    if (g_executable_path)
    {
        free ((void*)g_executable_path);
        g_executable_path = NULL;
    }
    if (g_coreclr_handle)
    {
        // Clean up some coreclr resources. This doesn't make coreclr unloadable.
        if (g_coreclr_shutdown)
            g_coreclr_shutdown (g_coreclr_handle, g_coreclr_domainId);
        g_coreclr_handle = NULL;
    }
    for (int i = 0; i < g_mapped_files_count; ++i)
    {
        munmap (g_mapped_files[i], g_mapped_file_sizes[i]);
        g_mapped_files[i] = NULL;
        g_mapped_file_sizes[i] = 0;
    }
    g_mapped_files_count = 0;
}

static int
coreclr_create_delegate_and_call (const char* assemblyName,
                                  const char* typeName,
                                  const char* methodName)
{
    LOG_INFO ("Creating delegate: %s.%s::%s", assemblyName, typeName, methodName);

    // Entry() → int (*fn)(void)
    typedef int (CORECLR_CALLING_CONVENTION *entry_fn)(void);
    entry_fn entry = NULL;

    int rc = g_coreclr_create_delegate(
        g_coreclr_handle, g_coreclr_domainId,
        assemblyName,
        typeName,
        methodName,
        (void**)&entry);

    if (rc < 0 || entry == NULL) {
        LOG_ERROR("coreclr_create_delegate failed: 0x%x", rc);
        return -1;
    }

    LOG_INFO ("Calling managed entry point...");
    int exitCode = entry();
    LOG_INFO ("Managed entry returned: %d", exitCode);
    return exitCode;
}

static int
coreclr_exec (const char* executable_path, int managed_argc, const char** managed_argv)
{
    unsigned int rv;
    LOG_INFO ("Calling coreclr_execute_assembly");
    g_coreclr_execute_assembly (g_coreclr_handle, g_coreclr_domainId, managed_argc, managed_argv, executable_path, &rv);
    LOG_INFO ("Exit code: %u.", rv);
    return (int)rv;
}

#define PROPERTY_COUNT 3

static int
mono_droid_runtime_init (const char* executable)
{
    LOG_INFO ("mono_droid_runtime_init (CoreCLR) called with executable: %s", executable);

    // build using DiagnosticPorts property in AndroidAppBuilder
    // or set DOTNET_DiagnosticPorts env via adb, xharness when undefined.
    // NOTE, using DOTNET_DiagnosticPorts requires app build using AndroidAppBuilder and RuntimeComponents to include 'diagnostics_tracing' component
#ifdef DIAGNOSTIC_PORTS
    setenv ("DOTNET_DiagnosticPorts", DIAGNOSTIC_PORTS, true);
#endif

    if (bundle_executable_path(executable, g_bundle_path, &g_executable_path) < 0)
    {
        LOG_ERROR("Failed to resolve full path for: %s", executable);
        return -1;
    }

    chdir (g_bundle_path);

    g_host_contract.external_assembly_probe = &external_assembly_probe;

    const char* appctx_keys[PROPERTY_COUNT];
    appctx_keys[0] = "RUNTIME_IDENTIFIER";
    appctx_keys[1] = "APP_CONTEXT_BASE_DIRECTORY";
    appctx_keys[2] = "HOST_RUNTIME_CONTRACT";

    const char* appctx_values[PROPERTY_COUNT];
    appctx_values[0] = ANDROID_RUNTIME_IDENTIFIER;
    appctx_values[1] = g_bundle_path;

    char contract_str[19];
    snprintf(contract_str, 19, "0x%zx", (size_t)(&g_host_contract));
    appctx_values[2] = contract_str;

    if (load_coreclr_runtime(g_bundle_path) != 0)
        return -1;

    LOG_INFO ("Calling coreclr_initialize");
    int rv = g_coreclr_initialize (
		g_executable_path,
		executable,
		PROPERTY_COUNT,
		appctx_keys,
		appctx_values,
		&g_coreclr_handle,
		&g_coreclr_domainId
		);
    LOG_INFO ("coreclr_initialize returned 0x%x", rv);
    return rv;
}

jint
Java_net_dot_MonoRunner_setEnv (JNIEnv* env, jclass thiz, jstring j_key, jstring j_value)
{
    LOG_INFO ("Java_net_dot_MonoRunner_setEnv:");
    if (g_coreclr_handle != NULL || g_coreclr_init_attempted)
    {
        LOG_ERROR("setEnv ignored after CoreCLR init attempt");
        return -1;
    }

    const char *key = (*env)->GetStringUTFChars(env, j_key, 0);
    const char *val = (*env)->GetStringUTFChars(env, j_value, 0);

    LOG_INFO ("Setting env: %s=%s", key, val);
    setenv (key, val, true);
    (*env)->ReleaseStringUTFChars(env, j_key, key);
    (*env)->ReleaseStringUTFChars(env, j_value, val);
    return 0;
}

int
Java_net_dot_MonoRunner_initRuntime (JNIEnv* env, jclass thiz, jstring j_files_dir, jstring j_entryPointLibName,
                                     jint current_local_time)
{
    LOG_INFO ("Java_net_dot_MonoRunner_initRuntime (CoreCLR):");
    if (g_coreclr_init_attempted)
    {
        LOG_ERROR("CoreCLR init requested more than once; returning previous result 0x%x", g_coreclr_init_result);
        return g_coreclr_init_result != 0 ? g_coreclr_init_result : -2;
    }
    g_coreclr_init_attempted = 1;

    if ((*env)->GetJavaVM(env, &g_java_vm) != JNI_OK || g_java_vm == NULL)
    {
        LOG_ERROR("Failed to obtain JavaVM before CoreCLR runtime initialization");
        g_coreclr_init_result = -1;
        return g_coreclr_init_result;
    }

    char file_dir[2048];
    char entryPointLibName[2048];
    strncpy_str (env, file_dir, j_files_dir, sizeof(file_dir));
    strncpy_str (env, entryPointLibName, j_entryPointLibName, sizeof(entryPointLibName));

    size_t file_dir_len = strlen(file_dir);
    char* bundle_path_tmp = (char*)malloc(sizeof(char) * (file_dir_len + 1)); // +1 for '\0'
    if (bundle_path_tmp == NULL)
    {
        LOG_ERROR("Failed to allocate memory for bundle_path");
        g_coreclr_init_result = -1;
        return g_coreclr_init_result;
    }
    strncpy(bundle_path_tmp, file_dir, file_dir_len + 1);
    g_bundle_path = bundle_path_tmp;

    g_coreclr_init_result = mono_droid_runtime_init (entryPointLibName);
    return g_coreclr_init_result;
}

int
Java_net_dot_MonoRunner_execEntryPoint (JNIEnv* env, jclass thiz,
    jstring j_entryPointLibName,
    jstring j_assemblyName, jstring j_typeName, jstring j_methodName)
{
    LOG_INFO("Java_net_dot_MonoRunner_execEntryPoint (CoreCLR create_delegate):");

    if ((g_coreclr_handle == NULL) || (g_coreclr_domainId == 0))
    {
        LOG_ERROR("CoreCLR not initialized");
        return -1;
    }

    char entryPointLibName[2048];
    char assemblyName[512];
    char typeName[512];
    char methodName[256];

    strncpy_str(env, entryPointLibName, j_entryPointLibName, sizeof(entryPointLibName));
    strncpy_str(env, assemblyName, j_assemblyName, sizeof(assemblyName));
    strncpy_str(env, typeName, j_typeName, sizeof(typeName));
    strncpy_str(env, methodName, j_methodName, sizeof(methodName));

    // Strip .dll from assembly name if present
    char* dot = strrchr(assemblyName, '.');
    if (dot && strcmp(dot, ".dll") == 0) *dot = '\0';

    return coreclr_create_delegate_and_call(assemblyName, typeName, methodName);
}

int
Java_net_dot_MonoRunner_execEntryPointWithArgs (JNIEnv* env, jclass thiz,
    jstring j_entryPointLibName,
    jstring j_assemblyName, jstring j_typeName, jstring j_methodName,
    jobjectArray j_args)
{
    LOG_INFO("Java_net_dot_MonoRunner_execEntryPointWithArgs (CoreCLR create_delegate with args):");

    if ((g_coreclr_handle == NULL) || (g_coreclr_domainId == 0))
    {
        LOG_ERROR("CoreCLR not initialized");
        return -1;
    }

    char entryPointLibName[2048];
    char assemblyName[512];
    char typeName[512];
    char methodName[256];

    strncpy_str(env, entryPointLibName, j_entryPointLibName, sizeof(entryPointLibName));
    strncpy_str(env, assemblyName, j_assemblyName, sizeof(assemblyName));
    strncpy_str(env, typeName, j_typeName, sizeof(typeName));
    strncpy_str(env, methodName, j_methodName, sizeof(methodName));

    // Strip .dll from assembly name if present
    char* dot = strrchr(assemblyName, '.');
    if (dot && strcmp(dot, ".dll") == 0) *dot = '\0';

    // 转换 Java String[] → const char* argv[]
    jsize argc = (*env)->GetArrayLength(env, j_args);
    const char** argv = NULL;
    jstring* argv_strings = NULL;
    if (argc > 0)
    {
        argv = (const char**)malloc(sizeof(const char*) * (size_t)argc);
        argv_strings = (jstring*)calloc((size_t)argc, sizeof(jstring));
        if (argv == NULL || argv_strings == NULL)
        {
            LOG_ERROR("Failed to allocate argv");
            free(argv);
            free(argv_strings);
            return -1;
        }
        for (jsize i = 0; i < argc; i++)
        {
            argv_strings[i] = (jstring)(*env)->GetObjectArrayElement(env, j_args, i);
            argv[i] = (*env)->GetStringUTFChars(env, argv_strings[i], NULL);
        }
    }

    LOG_INFO("Calling: %s.%s::%s with %d args", assemblyName, typeName, methodName, (int)argc);

    // 托管入口签名: static int MethodName(int argc, IntPtr argv)
    typedef int (CORECLR_CALLING_CONVENTION *entry_with_args_fn)(int, const char**);
    entry_with_args_fn entry = NULL;

    int rc = g_coreclr_create_delegate(
        g_coreclr_handle, g_coreclr_domainId,
        assemblyName,
        typeName,
        methodName,
        (void**)&entry);

    if (rc < 0 || entry == NULL)
    {
        LOG_ERROR("coreclr_create_delegate (with args) failed: 0x%x", rc);
        // Cleanup argv
        if (argv != NULL)
        {
            for (jsize i = 0; i < argc; i++)
            {
                if (argv_strings[i] != NULL && argv[i] != NULL)
                {
                    (*env)->ReleaseStringUTFChars(env, argv_strings[i], argv[i]);
                    (*env)->DeleteLocalRef(env, argv_strings[i]);
                }
            }
            free(argv);
            free(argv_strings);
        }
        return -1;
    }

    LOG_INFO("Calling managed entry point with args...");
    int exitCode = rc < 0 ? -1 : entry((int)argc, argv);
    LOG_INFO("Managed entry returned: %d", exitCode);

    // Cleanup argv
    if (argv != NULL)
    {
        for (jsize i = 0; i < argc; i++)
        {
            if (argv_strings[i] != NULL && argv[i] != NULL)
            {
                (*env)->ReleaseStringUTFChars(env, argv_strings[i], argv[i]);
                (*env)->DeleteLocalRef(env, argv_strings[i]);
            }
        }
        free(argv);
        free(argv_strings);
    }

    return exitCode;
}

void
Java_net_dot_MonoRunner_freeNativeResources (JNIEnv* env, jclass thiz)
{
    LOG_INFO ("Java_net_dot_MonoRunner_freeNativeResources (CoreCLR):");
    free_resources ();
}

jint
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeSetEnv(
    JNIEnv* env, jclass thiz, jstring j_key, jstring j_value)
{
    return Java_net_dot_MonoRunner_setEnv(env, thiz, j_key, j_value);
}

int
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeInitRuntime(
    JNIEnv* env, jclass thiz, jstring j_files_dir, jstring j_entryPointLibName, jint current_local_time)
{
    int result = Java_net_dot_MonoRunner_initRuntime(
        env, thiz, j_files_dir, j_entryPointLibName, current_local_time);
    if (result == 0) {
        modmanager_pccompat_start_hook_coordinator();
    }
    return result;
}

int
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeExecEntryPointWithArgs(
    JNIEnv* env,
    jclass thiz,
    jstring j_entryPointLibName,
    jstring j_assemblyName,
    jstring j_typeName,
    jstring j_methodName,
    jobjectArray j_args)
{
    return Java_net_dot_MonoRunner_execEntryPointWithArgs(
        env, thiz, j_entryPointLibName, j_assemblyName, j_typeName, j_methodName, j_args);
}

jboolean
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeConfigureAppFilesDir(
    JNIEnv* env, jclass thiz, jstring j_path)
{
    (void)thiz;
    if (j_path == NULL) {
        return JNI_FALSE;
    }
    const char *path = (*env)->GetStringUTFChars(env, j_path, NULL);
    if (path == NULL) {
        return JNI_FALSE;
    }
    int ok = modmanager_runtime_configure_app_files_dir(path);
    (*env)->ReleaseStringUTFChars(env, j_path, path);
    return ok ? JNI_TRUE : JNI_FALSE;
}

void
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeFreeNativeResources(
    JNIEnv* env, jclass thiz)
{
    Java_net_dot_MonoRunner_freeNativeResources(env, thiz);
}

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
#include "pccompat_open_runtime.h"
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
#include <time.h>
#include "hook_broker.h"
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
static coreclr_set_error_writer_ptr g_coreclr_set_error_writer = NULL;
static coreclr_shutdown_ptr g_coreclr_shutdown = NULL;
static coreclr_create_delegate_ptr g_coreclr_create_delegate = NULL;
static coreclr_execute_assembly_ptr g_coreclr_execute_assembly = NULL;
enum {
    CORECLR_INITIALIZE_DESCRIPTOR_SLOT = 0x72000001u,
    CORECLR_SET_ERROR_WRITER_DESCRIPTOR_SLOT = 0x72000002u,
    CORECLR_SHUTDOWN_DESCRIPTOR_SLOT = 0x72000003u,
    CORECLR_CREATE_DELEGATE_DESCRIPTOR_SLOT = 0x72000004u,
    CORECLR_EXECUTE_ASSEMBLY_DESCRIPTOR_SLOT = 0x72000005u,
    CORECLR_ANDROID_CRYPTO_JNI_ON_LOAD_DESCRIPTOR_SLOT = 0x72000006u,
    CORECLR_ANDROID_CRYPTO_VERIFY_DESCRIPTOR_SLOT = 0x72000007u,
};
static JavaVM* g_java_vm = NULL;
static void* g_android_crypto_library = NULL;
typedef jboolean (*android_verify_remote_certificate_fn)(
    JNIEnv*,
    jclass,
    jlong);
static android_verify_remote_certificate_fn g_verify_remote_certificate = NULL;

/*
 * The runtime crypto library is loaded from the extracted runtime directory by
 * native dlopen. Android's native-method resolver only searches libraries
 * associated with the Java ClassLoader, so expose a forwarding symbol from the
 * library loaded by MonoRunner and delegate to the runtime implementation.
 */
JNIEXPORT jboolean JNICALL
Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate(
    JNIEnv* env,
    jclass clazz,
    jlong ssl_stream_proxy_handle)
{
    if (g_verify_remote_certificate == NULL) {
        return JNI_FALSE;
    }
    return g_verify_remote_certificate(
        env,
        clazz,
        ssl_stream_proxy_handle);
}



#define MAX_MAPPED_COUNT 256 // Arbitrarily 'large enough' number
static void* g_mapped_files[MAX_MAPPED_COUNT];
static size_t g_mapped_file_sizes[MAX_MAPPED_COUNT];
static unsigned int g_mapped_files_count = 0;

static struct host_runtime_contract g_host_contract = {
    .size = sizeof(struct host_runtime_contract)
};

#define LOG_INFO(fmt, ...) __android_log_print(ANDROID_LOG_INFO, "DOTNET", fmt, ##__VA_ARGS__)
#define LOG_ERROR(fmt, ...) __android_log_print(ANDROID_LOG_ERROR, "DOTNET", fmt, ##__VA_ARGS__)

static int64_t
coreclr_debug_elapsed_ms(int64_t started_ns)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0)
        return -1;
    int64_t now_ns = (int64_t)now.tv_sec * INT64_C(1000000000) + now.tv_nsec;
    return (now_ns - started_ns) / INT64_C(1000000);
}

static int64_t
coreclr_debug_now_ns(void)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0)
        return 0;
    return (int64_t)now.tv_sec * INT64_C(1000000000) + now.tv_nsec;
}

static void
coreclr_debug_path(const char* label, const char* path)
{
    if (path == NULL || path[0] == '\0') {
        LOG_ERROR("[DEBUG-coreclr-init-v2] path label=%s value=<empty>",
                  label != NULL ? label : "<unknown>");
        return;
    }

    struct stat info;
    int stat_result = stat(path, &info);
    int stat_errno = stat_result == 0 ? 0 : errno;
    LOG_INFO("[DEBUG-coreclr-init-v2] path label=%s value=%s len=%zu stat=%d errno=%d "
             "mode=0%o size=%" PRId64,
             label != NULL ? label : "<unknown>",
             path,
             strlen(path),
             stat_result,
             stat_errno,
             stat_result == 0 ? (unsigned int)(info.st_mode & 07777) : 0U,
             stat_result == 0 ? (int64_t)info.st_size : INT64_C(-1));
}

static void
coreclr_debug_env(const char* name)
{
    const char* value = getenv(name);
    const size_t value_length = value != NULL ? strlen(value) : 0U;
    char preview[513];
    size_t preview_length = value_length > 512U ? 512U : value_length;
    if (preview_length > 0U)
        memcpy(preview, value, preview_length);
    preview[preview_length] = '\0';
    LOG_INFO("[DEBUG-coreclr-init-v2] env %s=%s%s len=%zu",
             name != NULL ? name : "<unknown>",
             value != NULL ? preview : "<unset>",
             value_length > 512U ? " [truncated]" : "",
             value_length);
}

static void
coreclr_error_writer(const char* message)
{
    LOG_ERROR("[DEBUG-coreclr-init-v2] coreclr-error %s", message != NULL ? message : "(null)");
}


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

static int
strncpy_str (JNIEnv *env, char *buff, jstring str, int nbuff)
{
    if (env == NULL || buff == NULL || nbuff <= 0 || str == NULL) {
        if (buff != NULL && nbuff > 0)
            buff[0] = '\0';
        LOG_ERROR("[DEBUG-coreclr-init-v2] JNI string conversion rejected env=%p buffer=%p "
                  "string=%p capacity=%d", env, buff, str, nbuff);
        return 0;
    }
    jboolean isCopy = 0;
    const char *copy_buff = (*env)->GetStringUTFChars (env, str, &isCopy);
    if (copy_buff == NULL) {
        buff[0] = '\0';
        LOG_ERROR("[DEBUG-coreclr-init-v2] JNI string conversion returned null string=%p",
                  str);
        return 0;
    }
    size_t source_length = strlen(copy_buff);
    if (source_length >= (size_t)nbuff) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] JNI string truncated length=%zu capacity=%d",
                  source_length, nbuff);
    }
    strncpy (buff, copy_buff, nbuff);
    buff[nbuff - 1] = '\0'; // ensure '\0' terminated
    if (isCopy)
        (*env)->ReleaseStringUTFChars (env, str, copy_buff);
    return source_length < (size_t)nbuff;
}

static int
bundle_executable_path (const char* executable, const char* bundle_path, const char** executable_path)
{
    if (executable == NULL || executable[0] == '\0' ||
        bundle_path == NULL || bundle_path[0] == '\0' || executable_path == NULL) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] executable path rejected executable=%s bundle=%s",
                  executable != NULL ? executable : "<null>",
                  bundle_path != NULL ? bundle_path : "<null>");
        return -1;
    }
    size_t executable_path_len = strlen(bundle_path) + strlen(executable) + 1; // +1 for '/'
    if (executable_path_len > 2047U) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] executable path too long length=%zu", executable_path_len);
        return -1;
    }
    char* temp_path = (char*)malloc(sizeof(char) * (executable_path_len + 1)); // +1 for '\0'
    if (temp_path == NULL)
    {
        LOG_ERROR("[DEBUG-coreclr-init-v2] executable path allocation failed length=%zu",
                  executable_path_len + 1U);
        return -1;
    }

    size_t res = snprintf(temp_path, (executable_path_len + 1), "%s/%s", bundle_path, executable);
    if (res < 0 || res != executable_path_len)
    {
        LOG_ERROR("[DEBUG-coreclr-init-v2] executable path formatting failed result=%d expected=%zu",
                  (int)res, executable_path_len);
        free(temp_path);
        return -1;
    }
    *executable_path = temp_path;
    return (int)executable_path_len;
}

static void*
load_runtime_library_optional(const char* bundle_path, const char* soname)
{
    int64_t started_ns = coreclr_debug_now_ns();
    char full_path[2048];
    if (bundle_path == NULL || bundle_path[0] == '\0' || soname == NULL || soname[0] == '\0') {
        LOG_ERROR("[DEBUG-coreclr-init-v2] runtime-library invalid-input bundle=%s soname=%s",
                  bundle_path != NULL ? bundle_path : "<null>",
                  soname != NULL ? soname : "<null>");
        return NULL;
    }
    int written = snprintf(full_path, sizeof(full_path), "%s/%s", bundle_path, soname);
    if (written <= 0 || written >= (int)sizeof(full_path))
    {
        LOG_ERROR("Runtime library path too long: %s", soname);
        return NULL;
    }

    void* handle = dlopen(full_path, RTLD_NOW | RTLD_GLOBAL);
    if (handle == NULL)
    {
        const char* error = dlerror();
        LOG_ERROR("[DEBUG-coreclr-init-v2] runtime-library name=%s path=%s handle=<null> "
                  "error=%s elapsedMs=%" PRId64,
                  soname,
                  full_path,
                  error != NULL ? error : "<none>",
                  coreclr_debug_elapsed_ms(started_ns));
    }
    else
    {
        coreclr_debug_path(soname, full_path);
        LOG_INFO("[DEBUG-coreclr-init-v2] runtime-library name=%s handle=%p elapsedMs=%" PRId64,
                 soname, handle, coreclr_debug_elapsed_ms(started_ns));
    }
    return handle;
}

static int
runtime_symbol_origin_matches(void* symbol, const char* expected_soname)
{
    Dl_info info;
    memset(&info, 0, sizeof(info));
    if (symbol == NULL || expected_soname == NULL ||
        dladdr(symbol, &info) == 0 || info.dli_fname == NULL)
        return 0;
    const char* base_name = strrchr(info.dli_fname, '/');
    base_name = base_name != NULL ? base_name + 1 : info.dli_fname;
    return strcmp(base_name, expected_soname) == 0;
}

static void*
resolve_runtime_callable_symbol(
    void* handle,
    const char* expected_soname,
    const char* name,
    uint32_t descriptor_slot)
{
    if (handle == NULL || expected_soname == NULL || name == NULL)
        return NULL;
    dlerror();
    void* symbol = dlsym(handle, name);
    const char* error = dlerror();
    if (error != NULL || symbol == NULL) {
        LOG_ERROR("Failed to resolve runtime symbol %s!%s: %s",
                  expected_soname,
                  name,
                  error != NULL ? error : "null");
        return NULL;
    }
    if (!runtime_symbol_origin_matches(symbol, expected_soname)) {
        LOG_ERROR("Runtime symbol origin mismatch: %s!%s", expected_soname, name);
        return NULL;
    }

    uintptr_t resolved_address = 0;
    if (!PC_COMPAT_RESOLVE_CONTINUATION(
            0,
            0,
            descriptor_slot,
            (uintptr_t)symbol,
            &resolved_address) ||
        resolved_address != (uintptr_t)symbol) {
        LOG_ERROR("Runtime symbol resolution failed: %s!%s",
                  expected_soname,
                  name);
        return NULL;
    }
    LOG_INFO("[DEBUG-coreclr-init-v2] symbol %s!%s raw=%p resolved=%p descriptor=0x%x",
             expected_soname, name, symbol, (void*)resolved_address, descriptor_slot);
    return (void*)resolved_address;
}

static int
initialize_runtime_jni_library(void* handle, const char* soname)
{
    LOG_INFO("[DEBUG-coreclr-init-v2] jni-runtime begin soname=%s handle=%p vm=%p",
             soname != NULL ? soname : "<null>", handle, g_java_vm);
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

    typedef jint (*jni_on_load_fn)(JavaVM*, void*);
    jni_on_load_fn on_load = (jni_on_load_fn)resolve_runtime_callable_symbol(
        handle,
        soname,
        "JNI_OnLoad",
        CORECLR_ANDROID_CRYPTO_JNI_ON_LOAD_DESCRIPTOR_SLOT);
    if (on_load == NULL)
    {
        LOG_ERROR("JNI runtime library %s has no protected JNI_OnLoad", soname);
        return -1;
    }

    jint version = on_load(g_java_vm, NULL);
    if (version == JNI_ERR)
    {
        LOG_ERROR("JNI_OnLoad failed for runtime library %s", soname);
        return -1;
    }

    LOG_INFO("[DEBUG-coreclr-init-v2] jni-runtime ready soname=%s version=0x%x", soname, version);
    return 0;
}

static void*
resolve_coreclr_symbol(const char* name, uint32_t descriptor_slot)
{
    return resolve_runtime_callable_symbol(
        g_coreclr_library,
        "libcoreclr.so",
        name,
        descriptor_slot);
}

static int
load_coreclr_runtime(const char* bundle_path)
{
    int64_t started_ns = coreclr_debug_now_ns();
    LOG_INFO("[DEBUG-coreclr-init-v2] runtime-load begin bundle=%s existingCoreclr=%p",
             bundle_path != NULL ? bundle_path : "<null>", g_coreclr_library);
    if (g_coreclr_library != NULL)
        return 0;

    load_runtime_library_optional(bundle_path, "libSystem.Native.so");
    load_runtime_library_optional(bundle_path, "libSystem.Globalization.Native.so");
    load_runtime_library_optional(bundle_path, "libSystem.IO.Compression.Native.so");
    g_android_crypto_library = load_runtime_library_optional(
        bundle_path,
        "libSystem.Security.Cryptography.Native.Android.so");
    if (g_android_crypto_library != NULL) {
        g_verify_remote_certificate =
            (android_verify_remote_certificate_fn)resolve_runtime_callable_symbol(
                g_android_crypto_library,
                "libSystem.Security.Cryptography.Native.Android.so",
                "Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate",
                CORECLR_ANDROID_CRYPTO_VERIFY_DESCRIPTOR_SLOT);
        if (g_verify_remote_certificate == NULL) {
            g_verify_remote_certificate = NULL;
            LOG_ERROR("Runtime crypto JNI verifier unavailable");
        } else {
            LOG_INFO("Runtime crypto JNI verifier forwarding enabled");
        }
    }
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

    LOG_INFO("[DEBUG-coreclr-init-v2] runtime-load coreclr handle=%p elapsedMs=%" PRId64,
             g_coreclr_library, coreclr_debug_elapsed_ms(started_ns));

    g_coreclr_initialize =
        (coreclr_initialize_ptr)resolve_coreclr_symbol(
            "coreclr_initialize", CORECLR_INITIALIZE_DESCRIPTOR_SLOT);
    g_coreclr_set_error_writer =
        (coreclr_set_error_writer_ptr)resolve_coreclr_symbol(
            "coreclr_set_error_writer", CORECLR_SET_ERROR_WRITER_DESCRIPTOR_SLOT);
    g_coreclr_shutdown =
        (coreclr_shutdown_ptr)resolve_coreclr_symbol(
            "coreclr_shutdown", CORECLR_SHUTDOWN_DESCRIPTOR_SLOT);
    g_coreclr_create_delegate =
        (coreclr_create_delegate_ptr)resolve_coreclr_symbol(
            "coreclr_create_delegate", CORECLR_CREATE_DELEGATE_DESCRIPTOR_SLOT);
    g_coreclr_execute_assembly =
        (coreclr_execute_assembly_ptr)resolve_coreclr_symbol(
            "coreclr_execute_assembly", CORECLR_EXECUTE_ASSEMBLY_DESCRIPTOR_SLOT);

    if (g_coreclr_initialize == NULL ||
        g_coreclr_set_error_writer == NULL ||
        g_coreclr_shutdown == NULL ||
        g_coreclr_create_delegate == NULL ||
        g_coreclr_execute_assembly == NULL)
    {
        LOG_ERROR("[DEBUG-coreclr-init-v2] runtime-load symbols incomplete initialize=%p "
                  "setError=%p shutdown=%p createDelegate=%p execute=%p",
                  g_coreclr_initialize,
                  g_coreclr_set_error_writer,
                  g_coreclr_shutdown,
                  g_coreclr_create_delegate,
                  g_coreclr_execute_assembly);
        dlclose(g_coreclr_library);
        g_coreclr_library = NULL;
        return -1;
    }

    LOG_INFO("[DEBUG-coreclr-init-v2] runtime-load symbols ready initialize=%p setError=%p "
             "shutdown=%p createDelegate=%p execute=%p elapsedMs=%" PRId64,
             g_coreclr_initialize,
             g_coreclr_set_error_writer,
             g_coreclr_shutdown,
             g_coreclr_create_delegate,
             g_coreclr_execute_assembly,
             coreclr_debug_elapsed_ms(started_ns));

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
    int64_t started_ns = coreclr_debug_now_ns();
    LOG_INFO ("[DEBUG-coreclr-init-v2] delegate begin assembly=%s type=%s method=%s "
              "handle=%p domain=%u",
              assemblyName != NULL ? assemblyName : "<null>",
              typeName != NULL ? typeName : "<null>",
              methodName != NULL ? methodName : "<null>",
              g_coreclr_handle,
              g_coreclr_domainId);

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
        LOG_ERROR("[DEBUG-coreclr-init-v2] delegate failed rc=0x%x ptr=%p elapsedMs=%" PRId64,
                  rc, entry, coreclr_debug_elapsed_ms(started_ns));
        return -1;
    }

    LOG_INFO ("[DEBUG-coreclr-init-v2] delegate ready rc=0x%x ptr=%p elapsedMs=%" PRId64,
              rc, entry, coreclr_debug_elapsed_ms(started_ns));
    LOG_INFO ("[DEBUG-coreclr-init-v2] managed entry begin");
    int exitCode = entry();
    LOG_INFO ("[DEBUG-coreclr-init-v2] managed entry end rv=%d elapsedMs=%" PRId64,
              exitCode, coreclr_debug_elapsed_ms(started_ns));
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
    int64_t started_ns = coreclr_debug_now_ns();
    LOG_INFO ("[DEBUG-coreclr-init-v2] init begin executable=%s bundle=%s attempted=%d "
              "handle=%p domain=%u",
              executable != NULL ? executable : "<null>",
              g_bundle_path != NULL ? g_bundle_path : "<null>",
              g_coreclr_init_attempted,
              g_coreclr_handle,
              g_coreclr_domainId);

    coreclr_debug_path("bundle", g_bundle_path);
    if (executable == NULL || executable[0] == '\0' ||
        g_bundle_path == NULL || g_bundle_path[0] == '\0') {
        LOG_ERROR("[DEBUG-coreclr-init-v2] init rejected empty executable or bundle path");
        return -1;
    }

    // build using DiagnosticPorts property in AndroidAppBuilder
    // or set DOTNET_DiagnosticPorts env via adb, xharness when undefined.
    // NOTE, using DOTNET_DiagnosticPorts requires app build using AndroidAppBuilder and RuntimeComponents to include 'diagnostics_tracing' component
#ifdef DIAGNOSTIC_PORTS
    setenv ("DOTNET_DiagnosticPorts", DIAGNOSTIC_PORTS, true);
#endif

    if (bundle_executable_path(executable, g_bundle_path, &g_executable_path) < 0)
    {
        LOG_ERROR("[DEBUG-coreclr-init-v2] init failed stage=resolve-executable code=-1 elapsedMs=%" PRId64,
                  coreclr_debug_elapsed_ms(started_ns));
        return -1;
    }

    coreclr_debug_path("executable", g_executable_path);

    errno = 0;
    int chdir_result = chdir (g_bundle_path);
    LOG_INFO("[DEBUG-coreclr-init-v2] cwd-change path=%s result=%d errno=%d",
             g_bundle_path, chdir_result, chdir_result == 0 ? 0 : errno);

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

    LOG_INFO("[DEBUG-coreclr-init-v2] contract address=%s size=%zu propertyCount=%d",
             contract_str, sizeof(g_host_contract), PROPERTY_COUNT);
    coreclr_debug_env("DOTNET_ROOT");
    coreclr_debug_env("TRUSTED_PLATFORM_ASSEMBLIES");
    coreclr_debug_env("APP_PATHS");
    coreclr_debug_env("NATIVE_DLL_SEARCH_DIRECTORIES");

    if (load_coreclr_runtime(g_bundle_path) != 0)
    {
        LOG_ERROR("[DEBUG-coreclr-init-v2] init failed stage=load-runtime code=-1 elapsedMs=%" PRId64,
                  coreclr_debug_elapsed_ms(started_ns));
        return -1;
    }

    int error_writer_result = g_coreclr_set_error_writer(coreclr_error_writer);
    char cwd[2048];
    const char* current_directory = getcwd(cwd, sizeof(cwd)) != NULL ? cwd : "(unavailable)";
    struct stat executable_stat;
    int executable_stat_result = stat(g_executable_path, &executable_stat);
    int executable_stat_errno = executable_stat_result == 0 ? 0 : errno;
    LOG_INFO("[DEBUG-coreclr-init-v2] writer=0x%x pageSize=%ld cwd=%s loadElapsedMs=%" PRId64,
             error_writer_result, sysconf(_SC_PAGESIZE), current_directory,
             coreclr_debug_elapsed_ms(started_ns));
    LOG_INFO("[DEBUG-coreclr-init-v2] executable=%s stat=%d errno=%d size=%" PRId64,
             g_executable_path,
             executable_stat_result,
             executable_stat_errno,
             executable_stat_result == 0 ? (int64_t)executable_stat.st_size : INT64_C(-1));
    for (int i = 0; i < PROPERTY_COUNT; i++) {
        LOG_INFO("[DEBUG-coreclr-init-v2] property[%d] %s=%s",
                 i, appctx_keys[i], appctx_values[i]);
    }

    LOG_INFO ("[DEBUG-coreclr-init-v2] coreclr_initialize begin exe=%s domainName=%s "
              "handleBefore=%p domainBefore=%u elapsedMs=%" PRId64,
              g_executable_path,
              executable,
              g_coreclr_handle,
              g_coreclr_domainId,
              coreclr_debug_elapsed_ms(started_ns));
    int64_t initialize_started_ns = coreclr_debug_now_ns();
    int rv = g_coreclr_initialize (
		g_executable_path,
		executable,
		PROPERTY_COUNT,
		appctx_keys,
		appctx_values,
		&g_coreclr_handle,
		&g_coreclr_domainId
		);
    LOG_INFO ("[DEBUG-coreclr-init-v2] coreclr_initialize end hr=0x%x "
              "win32=0x%x handleAfter=%p domainAfter=%u elapsedMs=%" PRId64 " totalMs=%" PRId64,
              rv,
              (unsigned int)((uint32_t)rv & UINT32_C(0xffff)),
              g_coreclr_handle,
              g_coreclr_domainId,
              coreclr_debug_elapsed_ms(initialize_started_ns),
              coreclr_debug_elapsed_ms(started_ns));
    if (rv != 0) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] init failed stage=coreclr_initialize hr=0x%x "
                  "win32=0x%x handle=%p domain=%u",
                  rv,
                  (unsigned int)((uint32_t)rv & UINT32_C(0xffff)),
                  g_coreclr_handle,
                  g_coreclr_domainId);
    }
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
    int64_t started_ns = coreclr_debug_now_ns();
    LOG_INFO ("[DEBUG-coreclr-init-v2] JNI init begin env=%p filesString=%p entryString=%p "
              "localOffset=%d attempted=%d",
              env, j_files_dir, j_entryPointLibName, current_local_time,
              g_coreclr_init_attempted);
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

    if (j_files_dir == NULL || j_entryPointLibName == NULL) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] JNI init rejected null path string files=%p entry=%p",
                  j_files_dir, j_entryPointLibName);
        g_coreclr_init_result = -4;
        return g_coreclr_init_result;
    }

    char file_dir[2048];
    char entryPointLibName[2048];
    int file_dir_ok = strncpy_str(env, file_dir, j_files_dir, sizeof(file_dir));
    int entry_library_ok = strncpy_str(
        env, entryPointLibName, j_entryPointLibName, sizeof(entryPointLibName));

    LOG_INFO("[DEBUG-coreclr-init-v2] JNI strings files=%s filesLen=%zu filesOk=%d "
             "entry=%s entryLen=%zu entryOk=%d",
             file_dir, strlen(file_dir), file_dir_ok,
             entryPointLibName, strlen(entryPointLibName), entry_library_ok);
    if (!file_dir_ok || !entry_library_ok || file_dir[0] == '\0' ||
        entryPointLibName[0] == '\0') {
        LOG_ERROR("[DEBUG-coreclr-init-v2] JNI init rejected invalid path string");
        g_coreclr_init_result = -5;
        return g_coreclr_init_result;
    }

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
    LOG_INFO("[DEBUG-coreclr-init-v2] JNI init end hr=0x%x handle=%p domain=%u elapsedMs=%" PRId64,
             g_coreclr_init_result, g_coreclr_handle, g_coreclr_domainId,
             coreclr_debug_elapsed_ms(started_ns));
    return g_coreclr_init_result;
}

int
Java_net_dot_MonoRunner_execEntryPoint (JNIEnv* env, jclass thiz,
    jstring j_entryPointLibName,
    jstring j_assemblyName, jstring j_typeName, jstring j_methodName)
{
    LOG_INFO("[DEBUG-coreclr-init-v2] JNI exec begin mode=no-args env=%p handle=%p domain=%u",
             env, g_coreclr_handle, g_coreclr_domainId);

    if ((g_coreclr_handle == NULL) || (g_coreclr_domainId == 0))
    {
        LOG_ERROR("CoreCLR not initialized");
        return -1;
    }

    char entryPointLibName[2048];
    char assemblyName[512];
    char typeName[512];
    char methodName[256];

    if (!strncpy_str(env, entryPointLibName, j_entryPointLibName, sizeof(entryPointLibName)) ||
        !strncpy_str(env, assemblyName, j_assemblyName, sizeof(assemblyName)) ||
        !strncpy_str(env, typeName, j_typeName, sizeof(typeName)) ||
        !strncpy_str(env, methodName, j_methodName, sizeof(methodName))) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] delegate request rejected invalid JNI string");
        return -2;
    }

    // Strip .dll from assembly name if present
    char* dot = strrchr(assemblyName, '.');
    if (dot && strcmp(dot, ".dll") == 0) *dot = '\0';

    int result = coreclr_create_delegate_and_call(assemblyName, typeName, methodName);
    LOG_INFO("[DEBUG-coreclr-init-v2] JNI exec end mode=no-args rv=%d", result);
    return result;
}

int
Java_net_dot_MonoRunner_execEntryPointWithArgs (JNIEnv* env, jclass thiz,
    jstring j_entryPointLibName,
    jstring j_assemblyName, jstring j_typeName, jstring j_methodName,
    jobjectArray j_args)
{
    LOG_INFO("[DEBUG-coreclr-init-v2] JNI exec begin mode=args env=%p handle=%p domain=%u",
             env, g_coreclr_handle, g_coreclr_domainId);

    if ((g_coreclr_handle == NULL) || (g_coreclr_domainId == 0))
    {
        LOG_ERROR("CoreCLR not initialized");
        return -1;
    }

    char entryPointLibName[2048];
    char assemblyName[512];
    char typeName[512];
    char methodName[256];

    if (!strncpy_str(env, entryPointLibName, j_entryPointLibName, sizeof(entryPointLibName)) ||
        !strncpy_str(env, assemblyName, j_assemblyName, sizeof(assemblyName)) ||
        !strncpy_str(env, typeName, j_typeName, sizeof(typeName)) ||
        !strncpy_str(env, methodName, j_methodName, sizeof(methodName))) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] delegate-with-args request rejected invalid JNI string");
        return -2;
    }

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

    LOG_INFO("[DEBUG-coreclr-init-v2] delegate-with-args request assembly=%s type=%s method=%s argc=%d",
             assemblyName, typeName, methodName, (int)argc);

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
        LOG_ERROR("[DEBUG-coreclr-init-v2] delegate-with-args failed rc=0x%x ptr=%p",
                  rc, entry);
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

    LOG_INFO("[DEBUG-coreclr-init-v2] managed entry begin mode=args ptr=%p argc=%d", entry, (int)argc);
    int exitCode = rc < 0 ? -1 : entry((int)argc, argv);
    LOG_INFO("[DEBUG-coreclr-init-v2] managed entry end mode=args rv=%d", exitCode);

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

jlong
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeGetHookBrokerApiV1(
    JNIEnv* env, jclass thiz)
{
    (void)env;
    (void)thiz;
    return (jlong)(uintptr_t)modmanager_hook_broker_get_api_v1();
}

int
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeInitRuntime(
    JNIEnv* env, jclass thiz, jstring j_files_dir, jstring j_entryPointLibName, jint current_local_time)
{
    int64_t started_ns = coreclr_debug_now_ns();
    LOG_INFO("[DEBUG-coreclr-init-v2] native-init begin env=%p filesString=%p entryString=%p "
             "localOffset=%d capability=%d attempted=%d",
             env,
             j_files_dir,
             j_entryPointLibName,
             current_local_time,
             pccompat_runtime_enabled(0),
             g_coreclr_init_attempted);
    if (!pccompat_runtime_enabled(0)) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] native-init rejected stage=capability hr=0x7001");
        return 0x7001;
    }
    if (j_files_dir == NULL || j_entryPointLibName == NULL) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] native-init rejected stage=arguments hr=0x7004");
        return 0x7004;
    }
    const char *runtime_root = (*env)->GetStringUTFChars(env, j_files_dir, NULL);
    if (runtime_root == NULL) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] native-init rejected stage=runtime-root-string hr=0x7004");
        return 0x7004;
    }
    coreclr_debug_path("native-runtime-root", runtime_root);
    (*env)->ReleaseStringUTFChars(env, j_files_dir, runtime_root);
    const char *entry_library =
        (*env)->GetStringUTFChars(env, j_entryPointLibName, NULL);
    if (entry_library == NULL) {
        LOG_ERROR("[DEBUG-coreclr-init-v2] native-init rejected stage=entry-library-string hr=0x7008");
        return 0x7008;
    }
    LOG_INFO("[DEBUG-coreclr-init-v2] input entry-library=%s len=%zu",
             entry_library, strlen(entry_library));
    LOG_INFO("[DEBUG-coreclr-init-v2] entry-library accepted value=%s", entry_library);
    (*env)->ReleaseStringUTFChars(env, j_entryPointLibName, entry_library);
    int result = Java_net_dot_MonoRunner_initRuntime(
        env, thiz, j_files_dir, j_entryPointLibName, current_local_time);
    if (result == 0 &&
        pccompat_runtime_enabled(0)) {
        modmanager_pccompat_start_hook_coordinator();
    }
    LOG_INFO("[DEBUG-coreclr-init-v2] native-init end hr=0x%x handle=%p domain=%u elapsedMs=%" PRId64,
             result, g_coreclr_handle, g_coreclr_domainId,
             coreclr_debug_elapsed_ms(started_ns));
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
    if (!pccompat_runtime_enabled(0)) {
        LOG_ERROR("managed entry rejected: modmanager_runtime capability unavailable");
        return 0x7002;
    }
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
    (*env)->ReleaseStringUTFChars(env, j_path, path);
    return JNI_TRUE;
}

jboolean
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeHasCapability(
    JNIEnv* env, jclass thiz, jlong capability)
{
    (void)env;
    (void)thiz;
    return pccompat_runtime_enabled((uint64_t)capability) ? JNI_TRUE : JNI_FALSE;
}

void
Java_com_fizzd_connectedworlds_editorport_StArrayModManagerBootstrap_nativeFreeNativeResources(
    JNIEnv* env, jclass thiz)
{
    Java_net_dot_MonoRunner_freeNativeResources(env, thiz);
}

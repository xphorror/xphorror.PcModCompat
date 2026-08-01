#include "runtime_il2cpp_bridge.h"

#include <android/log.h>
#include <dlfcn.h>
#include <errno.h>
#include <inttypes.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "adofai_app_files.h"

#define LOG_TAG "StArray.RuntimeBridge"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

#define ASYNC_PROVIDER_MAGIC "ADOASYNCIL2CPPHANDLE1"
#define ASYNC_PROVIDER_LIBRARY "libAsyncInput.so"
#define ASYNC_PROVIDER_SYMBOL "ADOFAIAsyncInputGetIl2CppHandleV1"

typedef void *(*Il2CppHandleProviderFn)(void);

static AdoAppFilesState g_app_files_state = ADO_APP_FILES_STATE_INITIALIZER;
static pthread_mutex_t g_path_lock = PTHREAD_MUTEX_INITIALIZER;
static char g_provider_pointer_path[PATH_MAX];
static pthread_mutex_t g_provider_lock = PTHREAD_MUTEX_INITIALIZER;
static Il2CppHandleProviderFn g_provider;
static _Atomic uint32_t g_unavailable_logged;

static uint64_t pointer_cookie(uintptr_t address, uint64_t pid) {
    uint64_t value = ((uint64_t)address) ^
        ((pid & UINT64_C(0xffffffff)) << 32) ^
        UINT64_C(0xa6d0f14c9e3779b9);
    value ^= value >> 33;
    value *= UINT64_C(0xff51afd7ed558ccd);
    value ^= value >> 33;
    value *= UINT64_C(0xc4ceb9fe1a85ec53);
    value ^= value >> 33;
    return value;
}

static int provider_origin_is_valid(void *symbol) {
    Dl_info info;
    memset(&info, 0, sizeof(info));
    if (symbol == NULL || dladdr(symbol, &info) == 0 ||
        info.dli_fname == NULL || info.dli_sname == NULL ||
        info.dli_saddr != symbol) {
        return 0;
    }
    const char *name = strrchr(info.dli_fname, '/');
    name = name != NULL ? name + 1 : info.dli_fname;
    return strcmp(name, ASYNC_PROVIDER_LIBRARY) == 0 &&
        strcmp(info.dli_sname, ASYNC_PROVIDER_SYMBOL) == 0;
}

static int parse_hex_pointer(const char *text, uintptr_t *value) {
    if (text == NULL || value == NULL || text[0] == '\0') return 0;
    errno = 0;
    char *end = NULL;
    unsigned long long parsed = strtoull(text, &end, 16);
    if (errno != 0 || end == text || *end != '\0' || parsed == 0 ||
        (uintmax_t)parsed > (uintmax_t)UINTPTR_MAX) {
        return 0;
    }
    *value = (uintptr_t)parsed;
    return 1;
}

static int parse_hex_u64(const char *text, uint64_t *value) {
    if (text == NULL || value == NULL || text[0] == '\0') return 0;
    errno = 0;
    char *end = NULL;
    unsigned long long parsed = strtoull(text, &end, 16);
    if (errno != 0 || end == text || *end != '\0') return 0;
    *value = (uint64_t)parsed;
    return 1;
}

int modmanager_runtime_configure_app_files_dir(const char *path) {
    char validated[PATH_MAX];
    if (!ado_app_files_validate(path, validated)) {
        return 0;
    }

    pthread_mutex_lock(&g_path_lock);
    char current[PATH_MAX];
    if (ado_app_files_get(&g_app_files_state, current, sizeof(current))) {
        int same = strcmp(current, validated) == 0;
        pthread_mutex_unlock(&g_path_lock);
        return same;
    }
    int ok = ado_app_files_join(
        g_provider_pointer_path,
        sizeof(g_provider_pointer_path),
        validated,
        "adofai_async_il2cpp_handle_provider.ptr");
    if (ok) {
        ok = ado_app_files_configure(&g_app_files_state, validated);
    }
    pthread_mutex_unlock(&g_path_lock);
    return ok;
}

static Il2CppHandleProviderFn load_provider(void) {
    char path[PATH_MAX];
    if (!ado_app_files_get(&g_app_files_state, path, sizeof(path))) {
        return NULL;
    }
    (void)path;

    FILE *file = g_provider_pointer_path[0] != '\0'
        ? fopen(g_provider_pointer_path, "r")
        : NULL;
    if (file == NULL) {
        return NULL;
    }

    char magic[32] = {0};
    char address_hex[32] = {0};
    char cookie_hex[32] = {0};
    long process_id = -1;
    int fields = fscanf(
        file, "%31s%ld%31s%31s",
        magic, &process_id, address_hex, cookie_hex);
    fclose(file);

    uintptr_t address = 0;
    uint64_t cookie = 0;
    uint64_t pid = (uint64_t)(uint32_t)getpid();
    if (fields != 4 || strcmp(magic, ASYNC_PROVIDER_MAGIC) != 0 ||
        process_id != (long)getpid() ||
        !parse_hex_pointer(address_hex, &address) ||
        !parse_hex_u64(cookie_hex, &cookie) ||
        cookie != pointer_cookie(address, pid) ||
        !provider_origin_is_valid((void *)address)) {
        return NULL;
    }
    return (Il2CppHandleProviderFn)address;
}

static Il2CppHandleProviderFn resolve_provider(void) {
    pthread_mutex_lock(&g_provider_lock);
    if (g_provider == NULL) {
        g_provider = load_provider();
        if (g_provider != NULL) {
            LOGI("IL2CPP handle provider resolved through AsyncInput");
        }
    }
    Il2CppHandleProviderFn provider = g_provider;
    pthread_mutex_unlock(&g_provider_lock);
    return provider;
}

int modmanager_runtime_enabled(void) {
    return 1;
}

void *modmanager_libil2cpp_handle(void) {
    Il2CppHandleProviderFn provider = resolve_provider();
    void *handle = provider != NULL ? provider() : NULL;
    if (handle == NULL) {
        if (atomic_exchange_explicit(
                &g_unavailable_logged, 1, memory_order_acq_rel) == 0) {
            LOGW("IL2CPP handle is not ready; consumer will retry");
        }
        return NULL;
    }

    void *domain_get = dlsym(handle, "il2cpp_domain_get");
    Dl_info info;
    memset(&info, 0, sizeof(info));
    if (domain_get == NULL || dladdr(domain_get, &info) == 0 ||
        info.dli_fname == NULL) {
        LOGW("IL2CPP handle rejected: il2cpp_domain_get unavailable");
        return NULL;
    }
    const char *name = strrchr(info.dli_fname, '/');
    name = name != NULL ? name + 1 : info.dli_fname;
    if (strcmp(name, "libil2cpp.so") != 0) {
        LOGW("IL2CPP handle rejected: unexpected origin %s", name);
        return NULL;
    }
    atomic_store_explicit(&g_unavailable_logged, 0, memory_order_release);
    return handle;
}

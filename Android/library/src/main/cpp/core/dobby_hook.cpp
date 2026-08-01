#include <jni.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <android/log.h>

#include <dobby.h>

#include "hook_broker.h"

#define LOG_TAG "StArray.ModManager.Dobby"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// ============================================================================
// C ABI exports for P/Invoke from C# (Mono)
// 命名规范: modmanager_dobby_xxx
// C# 通过 [DllImport("modmanager")] 直接调用
// ============================================================================
extern "C" {

/**
 * DobbyHook — 安装 inline hook。
 * @param address        目标函数地址
 * @param replace_func   替换函数地址
 * @param origin_func    [out] 保存原函数地址的指针
 * @return 0 成功，非 0 失败
 */
int modmanager_dobby_hook(void *address, void *replace_func, void **origin_func) {
    LOGI("HookBroker install at %p, replace=%p", address, replace_func);
    int ret = modmanager_hook_broker_install(
        "ModManager.ManagedDobby",
        address,
        replace_func,
        origin_func);
    if (ret != 0) LOGE("HookBroker install failed at %p, ret=%d", address, ret);
    return ret;
}

/**
 * DobbyInstrument — 安装动态指令插桩。
 * @param address      目标函数地址
 * @param pre_handler  前置回调 (dobby_instrument_callback_t)
 * @return 0 成功
 */
int modmanager_dobby_instrument(void *address, void *pre_handler) {
    LOGI("DobbyInstrument at %p, handler=%p", address, pre_handler);
    return DobbyInstrument(address, (dobby_instrument_callback_t)pre_handler);
}

/**
 * DobbyDestroy — 移除 hook 并恢复原函数。
 * @param address  被 hook 的函数地址
 * @return 0 成功
 */
int modmanager_dobby_destroy(void *address) {
    // HookBroker chains are permanent for the process lifetime. A chain also
    // patches prior detour heads, which are not root targets in the registry;
    // allowing Destroy for an "unknown" address could therefore dismantle a
    // continuation chain. Disable runtime unhook at the exported boundary.
    LOGE("DobbyDestroy rejected: runtime unhook is disabled (address=%p)", address);
    return -2;
}

/**
 * DobbySymbolResolver — 按 image 名称和 symbol 名称解析函数地址。
 * @param image_name   动态库名 (e.g., "libil2cpp.so")
 * @param symbol_name   符号名
 * @return 符号地址，失败返回 nullptr
 */
void *modmanager_dobby_symbol_resolver(const char *image_name, const char *symbol_name) {
    void *addr = DobbySymbolResolver(image_name, symbol_name);
    LOGI("DobbySymbolResolver(%s, %s) = %p", image_name, symbol_name, addr);
    return addr;
}

/**
 * DobbyCodePatch — 内存代码补丁。
 * @param address      目标地址
 * @param buffer       补丁数据
 * @param buffer_size  补丁数据大小
 * @return 0 成功
 */
int modmanager_dobby_code_patch(void *address, const uint8_t *buffer, uint32_t buffer_size) {
    LOGI("DobbyCodePatch at %p, size=%u", address, buffer_size);
    return DobbyCodePatch(address, (uint8_t *)buffer, buffer_size);
}

/**
 * DobbyGetVersion — 获取 Dobby 版本字符串。
 */
const char *modmanager_dobby_get_version(void) {
    return DobbyGetVersion();
}

/**
 * modmanager_log_write — write a line to Android logcat.
 * Called from C# via [DllImport("modmanager")].
 */
void modmanager_log_write(int prio, const char *tag, const char *msg) {
    constexpr size_t kMaxLogcatPayloadBytes = 3000;
    const char *safe_tag = tag ? tag : "ModManager";
    if (!msg || !*msg) {
        __android_log_write(prio, safe_tag, "");
        return;
    }

    const char *line = msg;
    while (*line) {
        const char *newline = std::strchr(line, '\n');
        size_t line_length = newline
            ? static_cast<size_t>(newline - line)
            : std::strlen(line);
        if (line_length > 0 && line[line_length - 1] == '\r')
            --line_length;

        if (line_length == 0) {
            __android_log_write(prio, safe_tag, "");
        } else {
            size_t offset = 0;
            while (offset < line_length) {
                const size_t remaining = line_length - offset;
                size_t chunk_length = remaining > kMaxLogcatPayloadBytes
                    ? kMaxLogcatPayloadBytes
                    : remaining;

                // Keep the next chunk on a UTF-8 code-point boundary.
                if (chunk_length < remaining) {
                    while (chunk_length > 0 &&
                           (static_cast<unsigned char>(line[offset + chunk_length]) & 0xC0u) == 0x80u) {
                        --chunk_length;
                    }
                    if (chunk_length == 0)
                        chunk_length = kMaxLogcatPayloadBytes;
                }

                const std::string chunk(line + offset, chunk_length);
                __android_log_write(prio, safe_tag, chunk.c_str());
                offset += chunk_length;
            }
        }

        if (!newline)
            break;
        line = newline + 1;
    }
}

} // extern "C"

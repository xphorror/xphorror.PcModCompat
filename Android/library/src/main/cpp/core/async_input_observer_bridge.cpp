#include "async_input_observer_bridge.h"

#include "async_input_observer_abi.h"
#include "hud_logic_worker.h"
#include "realtime_event_core.h"

#include <android/log.h>
#include <atomic>
#include <cstdint>
#include <dlfcn.h>
#include <mutex>

namespace starray::async_input_bridge {
namespace {

extern "C" int modmanager_modal_input_is_active(void);

constexpr const char *kLogTag = "StArray.AsyncInputBridge";
constexpr int32_t kVirtualKeyboardDeviceId = -1;

std::mutex g_registration_lock;
std::atomic<bool> g_registered{false};
std::atomic<bool> g_enabled{false};
std::atomic<uint64_t> g_async_producer_epoch{0};
using IsTestMacroEnabledFn = int (*)();
std::atomic<IsTestMacroEnabledFn> g_is_test_macro_enabled{nullptr};

void on_enabled_changed(void *, int32_t enabled, uint64_t producer_epoch) {
    g_async_producer_epoch.store(producer_epoch, std::memory_order_release);
    g_enabled.store(enabled != 0, std::memory_order_release);
    realtime::set_active_producer(
        enabled != 0
            ? realtime::InputProducer::AsyncInput
            : realtime::InputProducer::OfficialActivity);
}

bool accepts_async_event(uint64_t producer_epoch) {
    return g_enabled.load(std::memory_order_acquire) &&
        producer_epoch == g_async_producer_epoch.load(std::memory_order_acquire);
}

void on_touch(void *, const AdoAsyncRawTouchEventV1 *event) {
    if (event == nullptr ||
        event->struct_size != sizeof(AdoAsyncRawTouchEventV1) ||
        event->abi_version != ADOFAI_ASYNC_RAW_OBSERVER_ABI_V1 ||
        !accepts_async_event(event->producer_epoch) ||
        modmanager_modal_input_is_active() != 0) {
        return;
    }
    hud_logic::ensure_started();
    realtime::observe_touch_raw(
        realtime::InputProducer::AsyncInput,
        event->action,
        event->pointer_id,
        event->pointer_count,
        static_cast<int64_t>(event->raw_ns),
        event->x,
        event->y,
        event->viewport_width,
        event->viewport_height,
        event->source,
        event->device_id,
        static_cast<int32_t>(event->android_flags));
}

void on_key(void *, const AdoAsyncRawKeyEventV1 *event) {
    if (event == nullptr ||
        event->struct_size != sizeof(AdoAsyncRawKeyEventV1) ||
        event->abi_version != ADOFAI_ASYNC_RAW_OBSERVER_ABI_V1 ||
        event->device_id == kVirtualKeyboardDeviceId ||
        !accepts_async_event(event->producer_epoch)) {
        return;
    }
    hud_logic::ensure_started();
    realtime::observe_key_raw(
        realtime::InputProducer::AsyncInput,
        event->action,
        event->key_code,
        event->scan_code,
        event->meta_state,
        event->device_id,
        event->repeat_count,
        static_cast<int64_t>(event->raw_ns),
        event->source,
        event->android_flags);
}

}  // namespace

void ensure_registered() {
    if (g_registered.load(std::memory_order_acquire))
        return;

    std::lock_guard<std::mutex> guard(g_registration_lock);
    if (g_registered.load(std::memory_order_relaxed))
        return;

    void *handle = dlopen("libAsyncInput.so", RTLD_NOW | RTLD_NOLOAD);
    if (handle == nullptr)
        return;

    auto register_observer = reinterpret_cast<ADOFAIAsyncInputRegisterRawObserverV1Fn>(
        dlsym(handle, "ADOFAIAsyncInput_RegisterRawObserverV1"));
    if (register_observer == nullptr) {
        dlclose(handle);
        return;
    }

    const AdoAsyncRawObserverV1 observer{
        .struct_size = sizeof(AdoAsyncRawObserverV1),
        .abi_version = ADOFAI_ASYNC_RAW_OBSERVER_ABI_V1,
        .user_data = nullptr,
        .on_enabled_changed = on_enabled_changed,
        .on_touch = on_touch,
        .on_key = on_key,
    };
    if (register_observer(&observer) == 0) {
        dlclose(handle);
        return;
    }

    g_is_test_macro_enabled.store(
        reinterpret_cast<IsTestMacroEnabledFn>(
            dlsym(handle, "ADOFAIAsyncInput_IsTestMacroEnabled")),
        std::memory_order_release);
    g_registered.store(true, std::memory_order_release);
    __android_log_print(
        ANDROID_LOG_INFO,
        kLogTag,
        "registered raw observer ABI v1 enabled=%d",
        g_enabled.load(std::memory_order_acquire) ? 1 : 0);
}

bool registered() {
    return g_registered.load(std::memory_order_acquire);
}

bool enabled() {
    return g_enabled.load(std::memory_order_acquire);
}

bool test_macro_enabled() {
    const auto read_enabled = g_is_test_macro_enabled.load(std::memory_order_acquire);
    return read_enabled != nullptr && read_enabled() != 0;
}

}  // namespace starray::async_input_bridge

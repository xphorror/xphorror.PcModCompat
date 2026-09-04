#include "unity_presentation_sink.h"

#include "dobby_hook_internal.h"
#include "hook_broker.h"
#include "hud_deadline_scheduler.h"
#include "hud_logic_worker.h"
#include "pccompat_open_runtime.h"
#include "pccompat_metadata_resolver.h"
#include "unity_presentation_objects.h"

#include <android/log.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <limits>
#include <mutex>
#include <sys/syscall.h>
#include <unistd.h>
#include <vector>

static_assert(sizeof(starray::unity_presentation_sink::PcCompatPresentationSinkStatsV1) == 44);
static_assert(sizeof(starray::unity_presentation_sink::PcCompatPresentationSinkStatsV2) == 64);
static_assert(sizeof(starray::unity_presentation_sink::PcCompatPresentationSinkStatsV3) == 80);
static_assert(sizeof(starray::unity_presentation_sink::PcCompatPresentationSinkStatsV4) == 108);

#define LOG_TAG "StArray.PresentationSink"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

extern "C" int modmanager_modal_input_blocks_unity_event_system(void);

namespace starray::unity_presentation_sink {
namespace {

using StaticVoid0Fn = void (*)(void *);
using InstanceVoid0Fn = void (*)(void *, void *);
using UnityMainWorkCallback = void (*)();
using ManagedFrameCallback = void (*)();
using ManagedOnGUICallback = void (*)();
using ProcessEventFn = void (*)(int, void *, bool *, void *);
using BeginGUIFn = void (*)(int, int, int, void *);
using GetCachedFontAssetFn = void *(*)(void *, void *, void *);

struct ImGuiFontMappingSlot {
    std::atomic<void *> font{nullptr};
    std::atomic<void *> text_core_font_asset{nullptr};
};

std::mutex g_install_lock;
std::atomic<void *> g_primary_original{nullptr};
std::atomic<void *> g_fallback_original{nullptr};
std::atomic<uint32_t> g_installed{0};
std::atomic<uint32_t> g_primary_hook{0};
std::atomic<uint32_t> g_fallback_hook{0};
std::atomic<uint32_t> g_consume_opportunities{0};
std::atomic<uint32_t> g_snapshot_updates{0};
std::atomic<uint32_t> g_command_count{0};
std::atomic<uint32_t> g_unsupported_command_count{0};
std::atomic<uint32_t> g_last_publication_generation{0};
std::atomic<uint32_t> g_last_session_generation{0};
std::atomic<uint32_t> g_consuming{0};
std::atomic<uint32_t> g_last_consumed_generation{0};
std::atomic<uint32_t> g_stream_gap_count{0};
std::atomic<uint32_t> g_stream_faulted{0};
starray::hud_logic::PresentationSnapshot g_pending_snapshot{};
uint32_t g_pending_command_offset = 0;
uint32_t g_pending_snapshot_active = 0;
std::atomic<UnityMainWorkCallback> g_unity_main_work_callback{nullptr};
std::atomic<uint32_t> g_unity_main_work_requested{0};
std::atomic<ManagedFrameCallback> g_managed_frame_callback{nullptr};
std::atomic<uint32_t> g_managed_frame_mode{0};
std::atomic<uint32_t> g_managed_frame_active{0};
std::atomic<int64_t> g_managed_frame_next_pending_ns{0};
std::atomic<void *> g_ongui_process_original{nullptr};
std::atomic<void *> g_ongui_begin_original{nullptr};
std::atomic<uint32_t> g_ongui_process_hook{0};
std::atomic<uint32_t> g_ongui_begin_hook{0};
std::atomic<uint32_t> g_ongui_hook{0};
std::atomic<ManagedOnGUICallback> g_managed_ongui_callback{nullptr};
std::atomic<uint32_t> g_ongui_enabled{0};
std::atomic<uint32_t> g_ongui_active{0};
std::atomic<uint32_t> g_ongui_process_event_count{0};
std::atomic<uint32_t> g_ongui_begin_gui_count{0};
std::atomic<uint32_t> g_ongui_dispatch_count{0};
std::atomic<uint32_t> g_ongui_process_trace_budget{0};
std::atomic<uint32_t> g_ongui_begin_trace_budget{0};
std::atomic<uint32_t> g_ongui_dispatch_trace_budget{0};
std::atomic<int32_t> g_ongui_borrowed_instance_id{
    std::numeric_limits<int32_t>::min()};
std::atomic<int64_t> g_ongui_borrowed_last_dispatch_ns{0};
std::atomic<void *> g_text_core_font_original{nullptr};
std::atomic<uint32_t> g_text_core_font_hook{0};
std::atomic<void *> g_event_system_update_original{nullptr};
std::atomic<uint32_t> g_event_system_update_hook{0};
std::mutex g_imgui_font_mapping_write_lock;
std::array<ImGuiFontMappingSlot, 64> g_imgui_font_mappings{};
std::atomic<uint32_t> g_imgui_font_mapping_count{0};

thread_local uint64_t t_ongui_event_generation = 0;

constexpr int64_t kManagedPendingIntervalNs = 250'000'000;
constexpr int64_t kOnGUIBorrowedHostReselectNs = 250'000'000;
constexpr int32_t kNoBorrowedOnGUIInstance = std::numeric_limits<int32_t>::min();
constexpr uint32_t kMaxPresentationCommandsPerOpportunity = 16;
constexpr uint32_t kPrimaryContinuationDescriptorSlot = 0x70000001u;
constexpr uint32_t kFallbackContinuationDescriptorSlot = 0x70000002u;
constexpr uint32_t kBeginGuiContinuationDescriptorSlot = 0x70000003u;
constexpr uint32_t kProcessEventContinuationDescriptorSlot = 0x70000004u;
constexpr uint32_t kTextCoreContinuationDescriptorSlot = 0x70000005u;
constexpr uint32_t kEventSystemContinuationDescriptorSlot = 0x70000006u;
constexpr uint32_t kUnityMainWorkCallbackDescriptorSlot = 0x70000101u;
constexpr uint32_t kManagedFrameCallbackDescriptorSlot = 0x70000102u;
constexpr uint32_t kManagedOnGuiCallbackDescriptorSlot = 0x70000103u;
constexpr uint32_t kResourceResolverCallbackDescriptorSlot = 0x70000104u;

int64_t steady_now_ns() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

void *protect_pccompat_managed_callback(
    void *callback,
    uint32_t descriptor_slot,
    const char *name) {
    if (callback == nullptr)
        return nullptr;
    uintptr_t protected_callback = 0;
    if (PC_COMPAT_RESOLVE_CONTINUATION(
            0,
            0,
            descriptor_slot,
            reinterpret_cast<uintptr_t>(callback),
            &protected_callback) != 1 ||
        protected_callback != reinterpret_cast<uintptr_t>(callback)) {
        LOGE("managed callback descriptor rejected: %s", name);
        return nullptr;
    }
    return reinterpret_cast<void *>(protected_callback);
}

bool install_protected_presentation_hook(const char *owner,
                                         uint32_t descriptor_slot,
                                         void *target,
                                         void *detour,
                                         void **continuation,
                                         std::string &error) {
    if (continuation == nullptr) {
        error = "continuation output is null";
        return false;
    }
    *continuation = nullptr;
    const int result = modmanager_hook_broker_install_protected(
        owner,
        0,
        0,
        descriptor_slot,
        target,
        detour,
        continuation);
    if (result != 0 || *continuation == nullptr) {
        error = "hook broker install failed ret=" + std::to_string(result) +
            " broker=" +
            (modmanager_hook_broker_get_last_error() == nullptr
                ? std::string{}
                : modmanager_hook_broker_get_last_error());
        return false;
    }
    return true;
}

bool take_trace_budget(std::atomic<uint32_t> &budget) {
    auto remaining = budget.load(std::memory_order_relaxed);
    while (remaining != 0) {
        if (budget.compare_exchange_weak(
                remaining,
                remaining - 1,
                std::memory_order_relaxed,
                std::memory_order_relaxed)) {
            return true;
        }
    }
    return false;
}

long current_thread_id() {
    return static_cast<long>(syscall(__NR_gettid));
}

void dispatch_managed_frame() {
    const auto mode = g_managed_frame_mode.load(std::memory_order_acquire);
    if (mode == 0)
        return;
    if (mode == 1) {
        const int64_t now = steady_now_ns();
        auto next = g_managed_frame_next_pending_ns.load(std::memory_order_acquire);
        if (next > now)
            return;
        if (!g_managed_frame_next_pending_ns.compare_exchange_strong(
                next,
                now + kManagedPendingIntervalNs,
                std::memory_order_acq_rel,
                std::memory_order_acquire))
            return;
    }
    // Active mode is fed by exactly one installed Canvas hook. Gameplay telemetry
    // publishes frameCount at 10 Hz and stops outside levels, so it must not gate
    // the all-scene managed lifecycle used by persistent MOD HUDs and KeyViewers.
    const auto callback = g_managed_frame_callback.load(std::memory_order_acquire);
    if (callback == nullptr ||
        g_managed_frame_active.exchange(1, std::memory_order_acq_rel) != 0) {
        return;
    }
    struct ActiveReset final {
        ~ActiveReset() {
            g_managed_frame_active.store(0, std::memory_order_release);
        }
    } reset;
    callback();
}

struct SnapshotConsumeResult {
    bool completed = true;
    uint32_t applied_commands = 0;
};

SnapshotConsumeResult consume_snapshot_slice(
    const starray::hud_logic::PresentationSnapshot &snapshot,
    uint32_t start_index) {
    if (snapshot.available == 0)
        return SnapshotConsumeResult{};

    const uint32_t total = std::min<uint32_t>(
        snapshot.command_count,
        static_cast<uint32_t>(starray::hud_logic::kMaxDuePresentationTasks));
    if (start_index >= total) {
        (void)unity_presentation_objects::consume_snapshot_range(
            snapshot,
            start_index,
            0,
            true);
        return SnapshotConsumeResult{};
    }

    const uint32_t count = std::min<uint32_t>(
        total - start_index,
        kMaxPresentationCommandsPerOpportunity);
    const auto range_result = unity_presentation_objects::consume_snapshot_range(
        snapshot,
        start_index,
        count,
        start_index + count >= total);
    const uint32_t next_index = start_index + range_result.consumed_commands;
    if (range_result.deferred || next_index < total) {
        g_pending_snapshot = snapshot;
        g_pending_command_offset = next_index;
        g_pending_snapshot_active = 1;
        return SnapshotConsumeResult{
            .completed = false,
            .applied_commands = range_result.consumed_commands,
        };
    }

    g_pending_snapshot = {};
    g_pending_command_offset = 0;
    g_pending_snapshot_active = 0;
    return SnapshotConsumeResult{
        .completed = true,
        .applied_commands = range_result.consumed_commands,
    };
}

bool consume_pending_snapshot_slice() {
    if (g_pending_snapshot_active == 0)
        return false;

    const uint32_t pending_generation = g_pending_snapshot.publication_generation;
    const auto result = consume_snapshot_slice(
        g_pending_snapshot,
        g_pending_command_offset);
    g_command_count.fetch_add(result.applied_commands, std::memory_order_relaxed);
    if (!result.completed)
        return true;

    g_last_consumed_generation.store(
        pending_generation,
        std::memory_order_release);
    starray::hud_logic::acknowledge_presentation_generation(
        pending_generation);
    g_pending_snapshot = {};
    g_pending_command_offset = 0;
    g_pending_snapshot_active = 0;
    return true;
}

bool handle_pending_snapshot_superseded(uint32_t published_generation) {
    if (g_pending_snapshot_active == 0 ||
        published_generation == g_pending_snapshot.publication_generation) {
        return false;
    }

    const uint32_t pending_generation = g_pending_snapshot.publication_generation;
    starray::hud_logic::PresentationSnapshot next{};
    if (!starray::hud_logic::read_next_presentation_snapshot(
            pending_generation,
            next)) {
        // The history mutex is busy.  Do not apply stale pending commands until
        // we can prove whether a clear barrier or history gap superseded them.
        return true;
    }

    if (next.history_gap == 0 && next.available != 0)
        return false;

    g_pending_snapshot = {};
    g_pending_command_offset = 0;
    g_pending_snapshot_active = 0;
    g_last_publication_generation.store(
        next.publication_generation,
        std::memory_order_release);
    g_last_session_generation.store(
        next.session_generation,
        std::memory_order_release);
    if (next.history_gap != 0) {
        g_stream_gap_count.fetch_add(1, std::memory_order_relaxed);
        g_stream_faulted.store(1, std::memory_order_release);
        unity_presentation_objects::invalidate_all_runtime_graphs_on_unity_main();
        LOGE("pending presentation superseded by history gap pending=%u next=%u; stream failed closed",
             pending_generation,
             next.publication_generation);
    }
    if (next.available == 0)
        g_stream_faulted.store(0, std::memory_order_release);

    g_last_consumed_generation.store(
        next.publication_generation,
        std::memory_order_release);
    starray::hud_logic::acknowledge_presentation_generation(
        next.publication_generation);
    return true;
}

void consume_requested_unity_main_work() {
    if (g_unity_main_work_requested.exchange(0, std::memory_order_acq_rel) == 0)
        return;
    const auto callback = g_unity_main_work_callback.load(std::memory_order_acquire);
    if (callback != nullptr)
        callback();
}

// Managed OnGUI has an independent demand gate. IMGUI settings must continue
// drawing when no HUD, KeyViewer or managed frame callback is active.
void dispatch_managed_ongui() {
    if (g_ongui_enabled.load(std::memory_order_acquire) == 0)
        return;
    const auto callback = g_managed_ongui_callback.load(std::memory_order_acquire);
    if (callback == nullptr ||
        g_ongui_active.exchange(1, std::memory_order_acq_rel) != 0) {
        return;
    }
    const auto dispatch_count =
        g_ongui_dispatch_count.fetch_add(1, std::memory_order_relaxed) + 1;
    if (take_trace_budget(g_ongui_dispatch_trace_budget)) {
        LOGI("OnGUI managed dispatch count=%u generation=%llu tid=%ld",
             dispatch_count,
             static_cast<unsigned long long>(t_ongui_event_generation),
             current_thread_id());
    }
    callback();
    g_ongui_active.store(0, std::memory_order_release);
}

// ProcessEvent is optional telemetry. Some Unity 6000 Android player paths
// invoke BeginGUI directly and never pass through this exported method.
void gui_utility_process_event(int event_id,
                               void *native_event,
                               bool *result,
                               void *method_info) {
    const auto original = reinterpret_cast<ProcessEventFn>(
        g_ongui_process_original.load(std::memory_order_acquire));
    if (!pccompat_runtime_enabled(0)) {
        if (original != nullptr)
            original(event_id, native_event, result, method_info);
        return;
    }
    ++t_ongui_event_generation;
    const auto process_count =
        g_ongui_process_event_count.fetch_add(1, std::memory_order_relaxed) + 1;
    if (g_ongui_enabled.load(std::memory_order_acquire) != 0 &&
        take_trace_budget(g_ongui_process_trace_budget)) {
        LOGI("OnGUI ProcessEvent count=%u eventId=%d generation=%llu tid=%ld",
             process_count,
             event_id,
             static_cast<unsigned long long>(t_ongui_event_generation),
             current_thread_id());
    }

    if (original != nullptr)
        original(event_id, native_event, result, method_info);
}

void gui_utility_begin_gui(int skin_mode,
                           int instance_id,
                           int use_guilayout,
                           void *method_info) {
    const auto original = reinterpret_cast<BeginGUIFn>(
        g_ongui_begin_original.load(std::memory_order_acquire));
    if (original != nullptr)
        original(skin_mode, instance_id, use_guilayout, method_info);
    if (!pccompat_runtime_enabled(0))
        return;
    const auto begin_count =
        g_ongui_begin_gui_count.fetch_add(1, std::memory_order_relaxed) + 1;

    if (g_ongui_enabled.load(std::memory_order_acquire) == 0)
        return;
    if (use_guilayout == 0)
        return;

    auto selected_instance =
        g_ongui_borrowed_instance_id.load(std::memory_order_acquire);
    if (selected_instance == kNoBorrowedOnGUIInstance) {
        g_ongui_borrowed_instance_id.store(instance_id, std::memory_order_release);
        selected_instance = instance_id;
    } else if (selected_instance != instance_id) {
        const int64_t last_dispatch_ns =
            g_ongui_borrowed_last_dispatch_ns.load(std::memory_order_acquire);
        if (last_dispatch_ns != 0 &&
            steady_now_ns() - last_dispatch_ns < kOnGUIBorrowedHostReselectNs) {
            return;
        }
        g_ongui_borrowed_instance_id.store(instance_id, std::memory_order_release);
        selected_instance = instance_id;
    }

    const int64_t dispatch_now_ns = steady_now_ns();
    const int64_t previous_dispatch_ns =
        g_ongui_borrowed_last_dispatch_ns.exchange(
            dispatch_now_ns,
            std::memory_order_acq_rel);
    const int64_t dispatch_gap_us = previous_dispatch_ns == 0
        ? -1
        : (dispatch_now_ns - previous_dispatch_ns) / 1000;
    if (take_trace_budget(g_ongui_begin_trace_budget)) {
        LOGI("OnGUI BeginGUI count=%u skinMode=%d useGUILayout=%d instance=%d selected=%d processGeneration=%llu gapUs=%lld tid=%ld",
             begin_count,
             skin_mode,
             use_guilayout,
             instance_id,
             selected_instance,
             static_cast<unsigned long long>(t_ongui_event_generation),
             static_cast<long long>(dispatch_gap_us),
             current_thread_id());
    }
    dispatch_managed_ongui();
}

void *find_imgui_text_core_font_asset(void *font) {
    if (font == nullptr)
        return nullptr;
    const auto count = g_imgui_font_mapping_count.load(std::memory_order_acquire);
    for (uint32_t index = 0; index < count; ++index) {
        auto &slot = g_imgui_font_mappings[index];
        if (slot.font.load(std::memory_order_acquire) == font) {
            return slot.text_core_font_asset.load(std::memory_order_acquire);
        }
    }
    return nullptr;
}

void *text_settings_get_cached_font_asset(void *text_settings,
                                          void *font,
                                          void *method_info) {
    const auto original = reinterpret_cast<GetCachedFontAssetFn>(
        g_text_core_font_original.load(std::memory_order_acquire));
    if (!pccompat_runtime_enabled(0)) {
        return original == nullptr
            ? nullptr
            : original(text_settings, font, method_info);
    }
    if (void *mapped = find_imgui_text_core_font_asset(font); mapped != nullptr)
        return mapped;
    return original == nullptr
        ? nullptr
        : original(text_settings, font, method_info);
}

void event_system_update(void *instance, void *method_info) {
    const auto original = reinterpret_cast<InstanceVoid0Fn>(
        g_event_system_update_original.load(std::memory_order_acquire));
    if (!pccompat_runtime_enabled(0)) {
        if (original != nullptr)
            original(instance, method_info);
        return;
    }
    if (modmanager_modal_input_blocks_unity_event_system() != 0)
        return;
    if (original != nullptr)
        original(instance, method_info);
}

void consume_latest_snapshot() {
    unity_presentation_objects::drain_retired_on_unity_main();
    consume_requested_unity_main_work();
    unity_presentation_objects::drain_pending_resources_on_unity_main();
    dispatch_managed_frame();
    const uint32_t published_generation =
        starray::hud_logic::presentation_publication_generation();
    if (published_generation ==
        g_last_consumed_generation.load(std::memory_order_acquire))
        return;
    if (g_consuming.exchange(1, std::memory_order_acq_rel) != 0)
        return;
    struct ConsumingReset final {
        ~ConsumingReset() {
            g_consuming.store(0, std::memory_order_release);
        }
    } consuming_reset;

    g_consume_opportunities.fetch_add(1, std::memory_order_relaxed);
    if (handle_pending_snapshot_superseded(published_generation))
        return;
    if (consume_pending_snapshot_slice())
        return;

    for (size_t pass = 0;
         pass < starray::hud_logic::kPresentationSnapshotHistoryCapacity;
        ++pass) {
        starray::hud_logic::PresentationSnapshot snapshot{};
        const uint32_t requested_generation =
            g_last_consumed_generation.load(std::memory_order_acquire);
        const bool changed = starray::hud_logic::read_next_presentation_snapshot(
            requested_generation,
            snapshot);
        if (!changed)
            break;
        g_last_publication_generation.store(
            snapshot.publication_generation,
            std::memory_order_release);
        g_last_session_generation.store(
            snapshot.session_generation,
            std::memory_order_release);
        if (snapshot.history_gap != 0) {
            g_stream_gap_count.fetch_add(1, std::memory_order_relaxed);
            g_stream_faulted.store(1, std::memory_order_release);
            unity_presentation_objects::invalidate_all_runtime_graphs_on_unity_main();
            LOGE("presentation history gap detected requested=%u next=%u; stream failed closed",
                 requested_generation,
                 snapshot.publication_generation);
        }
        if (snapshot.available == 0) {
            g_stream_faulted.store(0, std::memory_order_release);
        } else if (g_stream_faulted.load(std::memory_order_acquire) == 0) {
            const auto result = consume_snapshot_slice(snapshot, 0);
            g_snapshot_updates.fetch_add(1, std::memory_order_relaxed);
            g_command_count.fetch_add(result.applied_commands, std::memory_order_relaxed);
            const auto object_stats = unity_presentation_objects::read_stats();
            g_unsupported_command_count.store(
                object_stats.unsupported_command_count,
                std::memory_order_release);
            if (!result.completed)
                return;
        }
        g_last_consumed_generation.store(
            snapshot.publication_generation,
            std::memory_order_release);
        starray::hud_logic::acknowledge_presentation_generation(
            snapshot.publication_generation);
        if (snapshot.publication_generation == published_generation)
            break;
    }
}

void canvas_send_pre_will_render_canvases(void *method_info) {
    const auto original = reinterpret_cast<StaticVoid0Fn>(
        g_primary_original.load(std::memory_order_acquire));
    if (pccompat_runtime_enabled(0))
        consume_latest_snapshot();
    if (original != nullptr)
        original(method_info);
}

void canvas_update_registry_perform_update(void *instance, void *method_info) {
    const auto original = reinterpret_cast<InstanceVoid0Fn>(
        g_fallback_original.load(std::memory_order_acquire));
    if (pccompat_runtime_enabled(0))
        consume_latest_snapshot();
    if (original != nullptr)
        original(instance, method_info);
}

bool resolve_and_install_primary(std::string &error) {
    pccompat_metadata::ResolvedMethod method;
    if (!pccompat_metadata::resolve_method(
            pccompat_metadata::MethodIdentity{
                .assembly_name = "UnityEngine.UIModule",
                .namespace_name = "UnityEngine",
                .type_name = "Canvas",
                .method_name = "SendPreWillRenderCanvases",
                .return_type = "System.Void",
                .parameter_types = {},
                .is_static = true,
            },
            method,
            error)) {
        return false;
    }

    void *continuation = nullptr;
    std::string install_error;
    if (!install_protected_presentation_hook(
            "PcCompat:UnityPresentationSink:Canvas",
            kPrimaryContinuationDescriptorSlot,
            method.function,
            reinterpret_cast<void *>(&canvas_send_pre_will_render_canvases),
            &continuation,
            install_error)) {
        error = "Canvas.SendPreWillRenderCanvases hook failed: " + install_error;
        return false;
    }

    g_primary_original.store(continuation, std::memory_order_release);
    g_primary_hook.store(1, std::memory_order_release);
    g_installed.store(1, std::memory_order_release);
    LOGI("installed Canvas.SendPreWillRenderCanvases presentation hook target=%p original=%p",
         method.function,
         continuation);
    return true;
}

bool resolve_and_install_fallback(std::string &error) {
    pccompat_metadata::ResolvedMethod method;
    if (!pccompat_metadata::resolve_method(
            pccompat_metadata::MethodIdentity{
                .assembly_name = "UnityEngine.UI",
                .namespace_name = "UnityEngine.UI",
                .type_name = "CanvasUpdateRegistry",
                .method_name = "PerformUpdate",
                .return_type = "System.Void",
                .parameter_types = {},
                .is_static = false,
            },
            method,
            error)) {
        return false;
    }

    void *continuation = nullptr;
    std::string install_error;
    if (!install_protected_presentation_hook(
            "PcCompat:UnityPresentationSink:CanvasUpdateRegistry",
            kFallbackContinuationDescriptorSlot,
            method.function,
            reinterpret_cast<void *>(&canvas_update_registry_perform_update),
            &continuation,
            install_error)) {
        error = "CanvasUpdateRegistry.PerformUpdate hook failed: " + install_error;
        return false;
    }

    g_fallback_original.store(continuation, std::memory_order_release);
    g_fallback_hook.store(1, std::memory_order_release);
    g_installed.store(1, std::memory_order_release);
    LOGI("installed CanvasUpdateRegistry.PerformUpdate presentation fallback target=%p original=%p",
         method.function,
         continuation);
    return true;
}

bool resolve_and_install_ongui(std::string &error) {
    if (g_ongui_hook.load(std::memory_order_acquire) != 0)
        return true;

    pccompat_metadata::ResolvedMethod begin_method;
    if (!pccompat_metadata::resolve_method(
            pccompat_metadata::MethodIdentity{
                .assembly_name = "UnityEngine.IMGUIModule",
                .namespace_name = "UnityEngine",
                .type_name = "GUIUtility",
                .method_name = "BeginGUI",
                .return_type = "System.Void",
                .parameter_types = {"System.Int32", "System.Int32", "System.Int32"},
                .is_static = true,
            },
            begin_method,
            error)) {
        return false;
    }

    void *begin_continuation = nullptr;
    std::string begin_install_error;
    if (!install_protected_presentation_hook(
            "PcCompat:UnityPresentationSink:GUIUtility.BeginGUI",
            kBeginGuiContinuationDescriptorSlot,
            begin_method.function,
            reinterpret_cast<void *>(&gui_utility_begin_gui),
            &begin_continuation,
            begin_install_error)) {
        error = "GUIUtility.BeginGUI hook failed: " + begin_install_error;
        return false;
    }
    g_ongui_begin_original.store(begin_continuation, std::memory_order_release);
    g_ongui_begin_hook.store(1, std::memory_order_release);

    pccompat_metadata::ResolvedMethod process_method;
    std::string process_resolve_error;
    if (!pccompat_metadata::resolve_method(
            pccompat_metadata::MethodIdentity{
                .assembly_name = "UnityEngine.IMGUIModule",
                .namespace_name = "UnityEngine",
                .type_name = "GUIUtility",
                .method_name = "ProcessEvent",
                .return_type = "System.Void",
                .parameter_types = {"System.Int32", "System.IntPtr", "System.Boolean&"},
                .is_static = true,
            },
            process_method,
            process_resolve_error)) {
        LOGE("GUIUtility.ProcessEvent telemetry resolve failed: %s",
             process_resolve_error.c_str());
    } else {
        void *process_continuation = nullptr;
        std::string process_install_error;
        if (!install_protected_presentation_hook(
                "PcCompat:UnityPresentationSink:GUIUtility.ProcessEvent",
                kProcessEventContinuationDescriptorSlot,
                process_method.function,
                reinterpret_cast<void *>(&gui_utility_process_event),
                &process_continuation,
                process_install_error)) {
            const auto process_error =
                "GUIUtility.ProcessEvent telemetry hook failed: " +
                process_install_error;
            LOGE("%s", process_error.c_str());
        } else {
            g_ongui_process_original.store(
                process_continuation,
                std::memory_order_release);
            g_ongui_process_hook.store(1, std::memory_order_release);
        }
    }
    g_ongui_hook.store(1, std::memory_order_release);
    LOGI("installed GUIUtility managed OnGUI hooks process=%p begin=%p",
         process_method.function,
         begin_method.function);
    return true;
}

bool resolve_and_install_text_core_font_cache(std::string &error) {
    if (g_text_core_font_hook.load(std::memory_order_acquire) != 0)
        return true;

    pccompat_metadata::ResolvedMethod method;
    if (!pccompat_metadata::resolve_method(
            pccompat_metadata::MethodIdentity{
                .assembly_name = "UnityEngine.TextCoreTextEngineModule",
                .namespace_name = "UnityEngine.TextCore.Text",
                .type_name = "TextSettings",
                .method_name = "GetCachedFontAsset",
                .return_type = "UnityEngine.TextCore.Text.FontAsset",
                .parameter_types = {"UnityEngine.Font"},
                .is_static = false,
            },
            method,
            error)) {
        return false;
    }

    void *continuation = nullptr;
    std::string install_error;
    if (!install_protected_presentation_hook(
            "PcCompat:UnityPresentationSink:TextSettings.GetCachedFontAsset",
            kTextCoreContinuationDescriptorSlot,
            method.function,
            reinterpret_cast<void *>(&text_settings_get_cached_font_asset),
            &continuation,
            install_error)) {
        error = "TextSettings.GetCachedFontAsset hook failed: " + install_error;
        return false;
    }

    g_text_core_font_original.store(continuation, std::memory_order_release);
    g_text_core_font_hook.store(1, std::memory_order_release);
    LOGI("installed TextSettings.GetCachedFontAsset bridge target=%p original=%p",
         method.function,
         continuation);
    return true;
}

bool resolve_and_install_event_system_gate(std::string &error) {
    if (g_event_system_update_hook.load(std::memory_order_acquire) != 0)
        return true;

    pccompat_metadata::ResolvedMethod method;
    if (!pccompat_metadata::resolve_method(
            pccompat_metadata::MethodIdentity{
                .assembly_name = "UnityEngine.UI",
                .namespace_name = "UnityEngine.EventSystems",
                .type_name = "EventSystem",
                .method_name = "Update",
                .return_type = "System.Void",
                .parameter_types = {},
                .is_static = false,
            },
            method,
            error)) {
        return false;
    }

    void *continuation = nullptr;
    std::string install_error;
    if (!install_protected_presentation_hook(
            "PcCompat:UnityPresentationSink:EventSystem.Update",
            kEventSystemContinuationDescriptorSlot,
            method.function,
            reinterpret_cast<void *>(&event_system_update),
            &continuation,
            install_error)) {
        error = "EventSystem.Update hook failed: " + install_error;
        return false;
    }

    g_event_system_update_original.store(continuation, std::memory_order_release);
    g_event_system_update_hook.store(1, std::memory_order_release);
    LOGI("installed EventSystem.Update modal input gate target=%p original=%p",
         method.function,
         continuation);
    return true;
}

void install_optional_managed_presentation_hooks() {
    std::string ongui_error;
    if (!resolve_and_install_ongui(ongui_error))
        LOGE("managed OnGUI hook unavailable (uGUI unaffected): %s", ongui_error.c_str());

    std::string font_error;
    if (!resolve_and_install_text_core_font_cache(font_error)) {
        LOGE("Unity 6 IMGUI TextCore font bridge unavailable: %s", font_error.c_str());
    }

    std::string event_system_error;
    if (!resolve_and_install_event_system_gate(event_system_error)) {
        LOGE("Unity EventSystem modal input gate unavailable: %s",
             event_system_error.c_str());
    }
}

}  // namespace

bool ensure_installed(std::string &error) {
    std::lock_guard<std::mutex> guard(g_install_lock);
    if (g_installed.load(std::memory_order_acquire) != 0) {
        install_optional_managed_presentation_hooks();
        error.clear();
        return true;
    }

    std::string primary_error;
    if (resolve_and_install_primary(primary_error)) {
        error.clear();
        // Managed OnGUI and its Unity 6 TextCore font bridge are optional:
        // mods using only uGUI are unaffected when either cannot be resolved.
        install_optional_managed_presentation_hooks();
        return true;
    }

    std::string fallback_error;
    if (resolve_and_install_fallback(fallback_error)) {
        error.clear();
        LOGI("primary presentation hook unavailable; fallback installed primary_error=%s",
             primary_error.c_str());
        install_optional_managed_presentation_hooks();
        return true;
    }

    error = "presentation sink unavailable; primary=" + primary_error +
        "; fallback=" + fallback_error;
    return false;
}

bool register_bundle_graph(
    uint32_t bundle_id,
    const std::string &mod_id,
    const std::vector<pccompat_recipe::UiObjectNode> &nodes,
    const std::vector<pccompat_recipe::UiResourceBinding> &resources,
    std::string &error) {
    return unity_presentation_objects::register_bundle_graph(
        bundle_id,
        mod_id,
        nodes,
        resources,
        error);
}

void discard_bundle_graph(uint32_t bundle_id) {
    unity_presentation_objects::discard_bundle_graph(bundle_id);
}

bool set_bundle_presentation_enabled(
    uint32_t bundle_id,
    bool enabled) {
    return unity_presentation_objects::set_bundle_presentation_enabled(
        bundle_id,
        enabled);
}

void clear_bundle_graphs() {
    unity_presentation_objects::clear_bundle_graphs();
}

SinkStats read_stats() {
    const auto object_stats = unity_presentation_objects::read_stats();
    return SinkStats{
        .installed = g_installed.load(std::memory_order_acquire),
        .primary_hook = g_primary_hook.load(std::memory_order_acquire),
        .fallback_hook = g_fallback_hook.load(std::memory_order_acquire),
        .consume_opportunities = g_consume_opportunities.load(std::memory_order_relaxed),
        .snapshot_updates = g_snapshot_updates.load(std::memory_order_relaxed),
        .command_count = g_command_count.load(std::memory_order_relaxed),
        .unsupported_command_count = g_unsupported_command_count.load(std::memory_order_relaxed),
        .last_publication_generation = g_last_publication_generation.load(std::memory_order_acquire),
        .last_session_generation = g_last_session_generation.load(std::memory_order_acquire),
        .registered_graph_count = object_stats.registered_graphs,
        .materialized_graph_count = object_stats.materialized_graphs,
        .graph_materialization_failures = object_stats.materialization_failures,
        .invalid_target_count = object_stats.invalid_target_count,
        .retired_graph_count = object_stats.retired_graphs,
        .presentation_history_overflow_count =
            starray::hud_logic::presentation_history_overflow_count(),
        .stream_gap_count = g_stream_gap_count.load(std::memory_order_relaxed),
        .stream_faulted = g_stream_faulted.load(std::memory_order_acquire),
        .ongui_hook = g_ongui_hook.load(std::memory_order_acquire),
        .ongui_process_hook = g_ongui_process_hook.load(std::memory_order_acquire),
        .ongui_begin_hook = g_ongui_begin_hook.load(std::memory_order_acquire),
        .ongui_enabled = g_ongui_enabled.load(std::memory_order_acquire),
        .ongui_process_event_count =
            g_ongui_process_event_count.load(std::memory_order_relaxed),
        .ongui_begin_gui_count =
            g_ongui_begin_gui_count.load(std::memory_order_relaxed),
        .ongui_dispatch_count =
            g_ongui_dispatch_count.load(std::memory_order_relaxed),
    };
}

}  // namespace starray::unity_presentation_sink

extern "C" int modmanager_pccompat_register_imgui_font_mapping(
    void *font,
    void *text_core_font_asset) {
    using namespace starray::unity_presentation_sink;
    if (font == nullptr || text_core_font_asset == nullptr)
        return -1;
    if (g_text_core_font_hook.load(std::memory_order_acquire) == 0)
        return -2;

    std::lock_guard<std::mutex> guard(g_imgui_font_mapping_write_lock);
    const auto count = g_imgui_font_mapping_count.load(std::memory_order_relaxed);
    for (uint32_t index = 0; index < count; ++index) {
        auto &slot = g_imgui_font_mappings[index];
        if (slot.font.load(std::memory_order_acquire) != font)
            continue;
        slot.text_core_font_asset.store(text_core_font_asset, std::memory_order_release);
        return 1;
    }
    if (count >= g_imgui_font_mappings.size())
        return -3;
    auto &slot = g_imgui_font_mappings[count];
    slot.text_core_font_asset.store(text_core_font_asset, std::memory_order_relaxed);
    slot.font.store(font, std::memory_order_release);
    g_imgui_font_mapping_count.store(count + 1, std::memory_order_release);
    return 1;
}

extern "C" int modmanager_pccompat_unregister_imgui_font_mapping(void *font) {
    using namespace starray::unity_presentation_sink;
    if (font == nullptr)
        return -1;
    std::lock_guard<std::mutex> guard(g_imgui_font_mapping_write_lock);
    const auto count = g_imgui_font_mapping_count.load(std::memory_order_relaxed);
    for (uint32_t index = 0; index < count; ++index) {
        auto &slot = g_imgui_font_mappings[index];
        if (slot.font.load(std::memory_order_acquire) != font)
            continue;
        slot.font.store(nullptr, std::memory_order_release);
        slot.text_core_font_asset.store(nullptr, std::memory_order_release);
        const auto last_index = count - 1;
        if (index != last_index) {
            auto &last = g_imgui_font_mappings[last_index];
            slot.text_core_font_asset.store(
                last.text_core_font_asset.load(std::memory_order_acquire),
                std::memory_order_relaxed);
            slot.font.store(
                last.font.load(std::memory_order_acquire),
                std::memory_order_release);
            last.font.store(nullptr, std::memory_order_release);
            last.text_core_font_asset.store(nullptr, std::memory_order_release);
        }
        g_imgui_font_mapping_count.store(last_index, std::memory_order_release);
        return 1;
    }
    return 0;
}

extern "C" void modmanager_pccompat_set_unity_main_work_callback(void *callback) {
    using namespace starray::unity_presentation_sink;
    callback = protect_pccompat_managed_callback(
        callback,
        kUnityMainWorkCallbackDescriptorSlot,
        "unity-main-work");
    g_unity_main_work_callback.store(
        reinterpret_cast<UnityMainWorkCallback>(callback),
        std::memory_order_release);
}

extern "C" int modmanager_pccompat_ensure_presentation_sink() {
    using namespace starray::unity_presentation_sink;
    std::string error;
    if (ensure_installed(error))
        return 1;
    LOGE("failed to ensure UnityMain work hook: %s", error.c_str());
    return 0;
}

extern "C" int modmanager_pccompat_is_presentation_sink_installed() {
    using namespace starray::unity_presentation_sink;
    return g_installed.load(std::memory_order_acquire) != 0 ? 1 : 0;
}

extern "C" int modmanager_pccompat_request_unity_main_work() {
    using namespace starray::unity_presentation_sink;
    if (g_unity_main_work_callback.load(std::memory_order_acquire) == nullptr)
        return -1;
    if (g_installed.load(std::memory_order_acquire) == 0)
        return -2;
    g_unity_main_work_requested.store(1, std::memory_order_release);
    return 1;
}

extern "C" void modmanager_pccompat_set_managed_frame_callback(void *callback) {
    using namespace starray::unity_presentation_sink;
    callback = protect_pccompat_managed_callback(
        callback,
        kManagedFrameCallbackDescriptorSlot,
        "managed-frame");
    g_managed_frame_callback.store(
        reinterpret_cast<ManagedFrameCallback>(callback),
        std::memory_order_release);
    if (callback == nullptr)
        g_managed_frame_mode.store(0, std::memory_order_release);
}

extern "C" void modmanager_pccompat_set_managed_ongui_callback(void *callback) {
    using namespace starray::unity_presentation_sink;
    callback = protect_pccompat_managed_callback(
        callback,
        kManagedOnGuiCallbackDescriptorSlot,
        "managed-ongui");
    g_managed_ongui_callback.store(
        reinterpret_cast<ManagedOnGUICallback>(callback),
        std::memory_order_release);
    if (callback == nullptr)
        g_ongui_enabled.store(0, std::memory_order_release);
}

extern "C" void modmanager_pccompat_set_managed_ongui_enabled(int enabled) {
    using namespace starray::unity_presentation_sink;
    const uint32_t next =
        enabled != 0 &&
                g_managed_ongui_callback.load(std::memory_order_acquire) != nullptr
            ? 1u
            : 0u;
    const auto previous = g_ongui_enabled.exchange(next, std::memory_order_acq_rel);
    if (previous == next)
        return;

    g_ongui_borrowed_instance_id.store(
        kNoBorrowedOnGUIInstance,
        std::memory_order_release);
    g_ongui_borrowed_last_dispatch_ns.store(0, std::memory_order_release);

    if (next != 0) {
        g_ongui_process_trace_budget.store(4, std::memory_order_relaxed);
        g_ongui_begin_trace_budget.store(4, std::memory_order_relaxed);
        g_ongui_dispatch_trace_budget.store(4, std::memory_order_relaxed);
    }
    LOGI("OnGUI gate enabled=%u hooks=%u/%u counts=%u/%u/%u",
         next,
         g_ongui_process_hook.load(std::memory_order_acquire),
         g_ongui_begin_hook.load(std::memory_order_acquire),
         g_ongui_process_event_count.load(std::memory_order_relaxed),
         g_ongui_begin_gui_count.load(std::memory_order_relaxed),
         g_ongui_dispatch_count.load(std::memory_order_relaxed));
}

extern "C" void modmanager_pccompat_set_managed_frame_enabled(int enabled) {
    starray::unity_presentation_sink::modmanager_pccompat_set_managed_frame_mode(
        enabled != 0 ? 2 : 0);
}

extern "C" void modmanager_pccompat_set_managed_frame_mode(int mode) {
    using namespace starray::unity_presentation_sink;
    const uint32_t normalized = mode <= 0 ? 0u : mode == 1 ? 1u : 2u;
    g_managed_frame_next_pending_ns.store(0, std::memory_order_release);
    g_managed_frame_mode.store(
        normalized != 0 &&
                g_managed_frame_callback.load(std::memory_order_acquire) != nullptr
            ? normalized
            : 0u,
        std::memory_order_release);
}

extern "C" void modmanager_pccompat_set_ui_resource_resolver(void *callback) {
    using namespace starray::unity_presentation_sink;
    callback = protect_pccompat_managed_callback(
        callback,
        kResourceResolverCallbackDescriptorSlot,
        "resource-resolver");
    starray::unity_presentation_objects::set_resource_resolver_callback(callback);
}

extern "C" void modmanager_pccompat_refresh_ui_resources() {
    starray::unity_presentation_objects::refresh_resources_on_unity_main();
}

extern "C" void modmanager_pccompat_clear_ui_resources_for_mod(const char *mod_id) {
    if (mod_id == nullptr)
        return;
    starray::unity_presentation_objects::clear_resources_for_mod_on_unity_main(mod_id);
}

extern "C" int modmanager_pccompat_read_presentation_sink_stats(
    void *output,
    uint32_t output_size) {
    using namespace starray::unity_presentation_sink;
    if (output == nullptr || output_size < sizeof(uint32_t) * 2)
        return -1;

    const auto *request = static_cast<const PcCompatPresentationSinkStatsV1 *>(output);
    const auto stats = read_stats();
    if (request->struct_size == sizeof(PcCompatPresentationSinkStatsV1) &&
        request->abi_version == kPresentationSinkStatsAbiVersionV1 &&
        output_size >= sizeof(PcCompatPresentationSinkStatsV1)) {
        PcCompatPresentationSinkStatsV1 snapshot{
            .struct_size = sizeof(PcCompatPresentationSinkStatsV1),
            .abi_version = kPresentationSinkStatsAbiVersionV1,
            .installed = stats.installed,
            .primary_hook = stats.primary_hook,
            .fallback_hook = stats.fallback_hook,
            .consume_opportunities = stats.consume_opportunities,
            .snapshot_updates = stats.snapshot_updates,
            .command_count = stats.command_count,
            .unsupported_command_count = stats.unsupported_command_count,
            .last_publication_generation = stats.last_publication_generation,
            .last_session_generation = stats.last_session_generation,
        };
        std::memcpy(output, &snapshot, sizeof(snapshot));
        return 1;
    }

    if (request->struct_size == sizeof(PcCompatPresentationSinkStatsV2) &&
        request->abi_version == kPresentationSinkStatsAbiVersionV2 &&
        output_size >= sizeof(PcCompatPresentationSinkStatsV2)) {
        PcCompatPresentationSinkStatsV2 snapshot{
            .struct_size = sizeof(PcCompatPresentationSinkStatsV2),
            .abi_version = kPresentationSinkStatsAbiVersionV2,
            .installed = stats.installed,
            .primary_hook = stats.primary_hook,
            .fallback_hook = stats.fallback_hook,
            .consume_opportunities = stats.consume_opportunities,
            .snapshot_updates = stats.snapshot_updates,
            .command_count = stats.command_count,
            .unsupported_command_count = stats.unsupported_command_count,
            .last_publication_generation = stats.last_publication_generation,
            .last_session_generation = stats.last_session_generation,
            .registered_graph_count = stats.registered_graph_count,
            .materialized_graph_count = stats.materialized_graph_count,
            .graph_materialization_failures = stats.graph_materialization_failures,
            .invalid_target_count = stats.invalid_target_count,
            .retired_graph_count = stats.retired_graph_count,
        };
        std::memcpy(output, &snapshot, sizeof(snapshot));
        return 1;
    }

    if (request->struct_size == sizeof(PcCompatPresentationSinkStatsV3) &&
        request->abi_version == kPresentationSinkStatsAbiVersionV3 &&
        output_size >= sizeof(PcCompatPresentationSinkStatsV3)) {
        PcCompatPresentationSinkStatsV3 snapshot{
            .struct_size = sizeof(PcCompatPresentationSinkStatsV3),
            .abi_version = kPresentationSinkStatsAbiVersionV3,
            .installed = stats.installed,
            .primary_hook = stats.primary_hook,
            .fallback_hook = stats.fallback_hook,
            .consume_opportunities = stats.consume_opportunities,
            .snapshot_updates = stats.snapshot_updates,
            .command_count = stats.command_count,
            .unsupported_command_count = stats.unsupported_command_count,
            .last_publication_generation = stats.last_publication_generation,
            .last_session_generation = stats.last_session_generation,
            .registered_graph_count = stats.registered_graph_count,
            .materialized_graph_count = stats.materialized_graph_count,
            .graph_materialization_failures = stats.graph_materialization_failures,
            .invalid_target_count = stats.invalid_target_count,
            .retired_graph_count = stats.retired_graph_count,
            .presentation_history_overflow_count = stats.presentation_history_overflow_count,
            .stream_gap_count = stats.stream_gap_count,
            .stream_faulted = stats.stream_faulted,
        };
        std::memcpy(output, &snapshot, sizeof(snapshot));
        return 1;
    }

    if (request->struct_size != sizeof(PcCompatPresentationSinkStatsV4) ||
        request->abi_version != kPresentationSinkStatsAbiVersionV4 ||
        output_size < sizeof(PcCompatPresentationSinkStatsV4))
        return -1;

    PcCompatPresentationSinkStatsV4 snapshot{
        .struct_size = sizeof(PcCompatPresentationSinkStatsV4),
        .abi_version = kPresentationSinkStatsAbiVersionV4,
        .installed = stats.installed,
        .primary_hook = stats.primary_hook,
        .fallback_hook = stats.fallback_hook,
        .consume_opportunities = stats.consume_opportunities,
        .snapshot_updates = stats.snapshot_updates,
        .command_count = stats.command_count,
        .unsupported_command_count = stats.unsupported_command_count,
        .last_publication_generation = stats.last_publication_generation,
        .last_session_generation = stats.last_session_generation,
        .registered_graph_count = stats.registered_graph_count,
        .materialized_graph_count = stats.materialized_graph_count,
        .graph_materialization_failures = stats.graph_materialization_failures,
        .invalid_target_count = stats.invalid_target_count,
        .retired_graph_count = stats.retired_graph_count,
        .presentation_history_overflow_count = stats.presentation_history_overflow_count,
        .stream_gap_count = stats.stream_gap_count,
        .stream_faulted = stats.stream_faulted,
        .ongui_hook = stats.ongui_hook,
        .ongui_process_hook = stats.ongui_process_hook,
        .ongui_begin_hook = stats.ongui_begin_hook,
        .ongui_enabled = stats.ongui_enabled,
        .ongui_process_event_count = stats.ongui_process_event_count,
        .ongui_begin_gui_count = stats.ongui_begin_gui_count,
        .ongui_dispatch_count = stats.ongui_dispatch_count,
    };
    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

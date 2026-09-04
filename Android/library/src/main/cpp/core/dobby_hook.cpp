#include <jni.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <unistd.h>
#include <utility>
#include <vector>
#include <android/log.h>
#include "pccompat_open_runtime.h"

#include <dobby.h>

#include "hook_broker.h"
#include "dobby_hook_internal.h"
#include "native_patch_coordinator.h"

#define LOG_TAG "StArray.ModManager.Dobby"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace {

constexpr size_t kInstrumentSlotCount = 64;
constexpr auto kInstrumentQuiescenceTimeout = std::chrono::seconds(5);

struct InstrumentSlot {
    std::string owner;
    uint64_t generation = 0;
    void *target = nullptr;
    std::atomic<uintptr_t> handler{0};
    std::atomic<uint32_t> enabled{0};
    std::atomic<uint32_t> retired{0};
    std::atomic<uint32_t> active_callbacks{0};
};

std::mutex g_instrument_lock;
std::array<InstrumentSlot, kInstrumentSlotCount> g_instrument_slots;

bool protect_modmanager_tool_executable(
    const char *owner,
    uint64_t generation,
    void *candidate,
    uint32_t slot_discriminator,
    bool require_il2cpp_commitment,
    void **protected_out);

template <size_t SlotIndex>
void instrument_slot_dispatch(void *address, DobbyRegisterContext *context) {
    auto &slot = g_instrument_slots[SlotIndex];
    if (slot.enabled.load(std::memory_order_acquire) == 0)
        return;

    slot.active_callbacks.fetch_add(1, std::memory_order_acq_rel);
    if (slot.enabled.load(std::memory_order_acquire) == 0) {
        slot.active_callbacks.fetch_sub(1, std::memory_order_acq_rel);
        return;
    }

    const uintptr_t raw_handler = slot.handler.load(std::memory_order_acquire);
    if (raw_handler != 0) {
        reinterpret_cast<dobby_instrument_callback_t>(raw_handler)(address, context);
    }
    slot.active_callbacks.fetch_sub(1, std::memory_order_acq_rel);
}

template <size_t... Indices>
constexpr auto make_instrument_dispatchers(std::index_sequence<Indices...>) {
    return std::array<dobby_instrument_callback_t, sizeof...(Indices)>{
        &instrument_slot_dispatch<Indices>...};
}

constexpr auto kInstrumentDispatchers = make_instrument_dispatchers(
    std::make_index_sequence<kInstrumentSlotCount>{});

bool instrument_slot_matches(
    const InstrumentSlot &slot,
    const char *owner,
    uint64_t generation) {
    return slot.generation == generation && slot.owner == owner;
}

bool wait_instrument_slots_quiesced(const std::array<bool, kInstrumentSlotCount> &selected) {
    const auto deadline = std::chrono::steady_clock::now() +
        kInstrumentQuiescenceTimeout;
    for (;;) {
        bool quiesced = true;
        for (size_t index = 0; index < selected.size(); ++index) {
            if (selected[index] &&
                g_instrument_slots[index].active_callbacks.load(
                    std::memory_order_acquire) != 0) {
                quiesced = false;
                break;
            }
        }
        if (quiesced)
            return true;
        if (std::chrono::steady_clock::now() >= deadline)
            return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}

void restore_instrument_slots_enabled(
    const std::array<bool, kInstrumentSlotCount> &selected,
    const std::array<uint32_t, kInstrumentSlotCount> &previous_enabled) {
    for (size_t index = 0; index < selected.size(); ++index) {
        if (selected[index]) {
            g_instrument_slots[index].enabled.store(
                previous_enabled[index], std::memory_order_release);
        }
    }
}

int set_instrument_generation_enabled(
    const char *owner,
    uint64_t generation,
    bool enabled) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return -1;

    std::array<bool, kInstrumentSlotCount> selected{};
    std::array<uint32_t, kInstrumentSlotCount> previous_enabled{};
    int changed = 0;
    std::unique_lock<std::mutex> guard(g_instrument_lock);
    if (enabled) {
        for (const auto &slot : g_instrument_slots) {
            if (!instrument_slot_matches(slot, owner, generation) ||
                slot.retired.load(std::memory_order_acquire) != 0 ||
                slot.enabled.load(std::memory_order_acquire) != 0) {
                continue;
            }
            const uintptr_t handler = slot.handler.load(std::memory_order_acquire);
            void *protected_handler = nullptr;
            if (handler == 0 ||
                !protect_modmanager_tool_executable(
                    owner,
                    generation,
                    reinterpret_cast<void *>(handler),
                    UINT32_C(0x00FFFF12),
                    false,
                    &protected_handler) ||
                protected_handler != reinterpret_cast<void *>(handler)) {
                LOGE("DobbyInstrument generation enable descriptor rejected owner=%s generation=%llu target=%p",
                     owner,
                     static_cast<unsigned long long>(generation),
                     slot.target);
                return -3;
            }
        }
    }
    for (size_t index = 0; index < g_instrument_slots.size(); ++index) {
        auto &slot = g_instrument_slots[index];
        if (!instrument_slot_matches(slot, owner, generation) ||
            slot.retired.load(std::memory_order_acquire) != 0) {
            continue;
        }
        selected[index] = true;
        const uint32_t next = enabled ? 1u : 0u;
        previous_enabled[index] = slot.enabled.exchange(
            next, std::memory_order_acq_rel);
        if (previous_enabled[index] != next)
            ++changed;
    }
    if (!enabled && !wait_instrument_slots_quiesced(selected)) {
        restore_instrument_slots_enabled(selected, previous_enabled);
        LOGE("DobbyInstrument generation suspend timed out; state restored owner=%s generation=%llu",
             owner,
             static_cast<unsigned long long>(generation));
        return -2;
    }
    return changed;
}

int retire_instrument_generation(
    const char *owner,
    uint64_t generation,
    void *target) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return -1;

    std::array<bool, kInstrumentSlotCount> selected{};
    std::array<uint32_t, kInstrumentSlotCount> previous_enabled{};
    int retired = 0;
    std::unique_lock<std::mutex> guard(g_instrument_lock);
    for (size_t index = 0; index < g_instrument_slots.size(); ++index) {
        auto &slot = g_instrument_slots[index];
        if (!instrument_slot_matches(slot, owner, generation) ||
            (target != nullptr && slot.target != target) ||
            slot.retired.load(std::memory_order_acquire) != 0) {
            continue;
        }
        selected[index] = true;
        previous_enabled[index] = slot.enabled.exchange(
            0, std::memory_order_acq_rel);
        ++retired;
    }
    if (!wait_instrument_slots_quiesced(selected)) {
        restore_instrument_slots_enabled(selected, previous_enabled);
        LOGE("DobbyInstrument generation retirement timed out; state restored owner=%s generation=%llu",
             owner,
             static_cast<unsigned long long>(generation));
        return -2;
    }
    for (size_t index = 0; index < selected.size(); ++index) {
        if (!selected[index])
            continue;
        auto &slot = g_instrument_slots[index];
        slot.handler.store(0, std::memory_order_release);
        slot.retired.store(1, std::memory_order_release);
    }
    return retired;
}

int count_instrument_generation(const char *owner, uint64_t generation) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return 0;
    std::lock_guard<std::mutex> guard(g_instrument_lock);
    int count = 0;
    for (const auto &slot : g_instrument_slots) {
        if (instrument_slot_matches(slot, owner, generation) &&
            slot.retired.load(std::memory_order_acquire) == 0) {
            ++count;
        }
    }
    return count;
}

struct CodePatchLayer {
    std::string owner;
    uint64_t generation = 0;
    uint64_t sequence = 0;
    std::vector<uint8_t> bytes;
    bool enabled = false;
    bool retired = false;
};

struct CodePatchTarget {
    void *address = nullptr;
    uint32_t size = 0;
    std::vector<uint8_t> original;
    std::vector<CodePatchLayer> layers;
};

struct CodePatchPageSnapshot {
    uintptr_t page_base = 0;
    size_t page_size = 0;
    std::vector<uint8_t> pristine;
    std::vector<uint8_t> expected_current;
    bool authenticated_pristine = false;
};

struct CodePatchLayerState {
    size_t target_index = 0;
    size_t layer_index = 0;
    bool enabled = false;
};

std::mutex g_code_patch_lock;
std::vector<CodePatchTarget> g_code_patch_targets;
std::vector<CodePatchPageSnapshot> g_code_patch_page_snapshots;
uint64_t g_code_patch_sequence = 0;

bool code_patch_range(
    void *address,
    uint32_t size,
    uintptr_t &begin,
    uintptr_t &end) {
    if (address == nullptr || size == 0)
        return false;
    begin = reinterpret_cast<uintptr_t>(address);
    if (begin > UINTPTR_MAX - static_cast<uintptr_t>(size))
        return false;
    end = begin + static_cast<uintptr_t>(size);
    return true;
}

bool code_patch_ranges_overlap(
    uintptr_t left_begin,
    uintptr_t left_end,
    uintptr_t right_begin,
    uintptr_t right_end) {
    return left_begin < right_end && right_begin < left_end;
}

bool address_is_in_libil2cpp(void *address) {
    Dl_info info{};
    if (address == nullptr || dladdr(address, &info) == 0 ||
        info.dli_fname == nullptr) {
        return false;
    }
    const char *base_name = std::strrchr(info.dli_fname, '/');
    base_name = base_name == nullptr ? info.dli_fname : base_name + 1;
    return std::strcmp(base_name, "libil2cpp.so") == 0;
}

uint32_t code_patch_descriptor_slot(
    const char *owner,
    uint64_t generation,
    uintptr_t candidate,
    uint32_t size) {
    uint32_t hash = UINT32_C(2166136261);
    for (const auto *cursor = reinterpret_cast<const uint8_t *>(owner);
         *cursor != 0;
         ++cursor) {
        hash ^= *cursor;
        hash *= UINT32_C(16777619);
    }
    for (size_t index = 0; index < sizeof(generation); ++index) {
        hash ^= static_cast<uint8_t>(generation >> (index * 8U));
        hash *= UINT32_C(16777619);
    }
    for (size_t index = 0; index < sizeof(candidate); ++index) {
        hash ^= static_cast<uint8_t>(candidate >> (index * 8U));
        hash *= UINT32_C(16777619);
    }
    hash ^= size;
    hash *= UINT32_C(16777619);
    return UINT32_C(0x50000000) | (hash & UINT32_C(0x0FFFFFFF));
}

bool protect_modmanager_tool_executable(
    const char *owner,
    uint64_t generation,
    void *candidate,
    uint32_t slot_discriminator,
    bool require_il2cpp_commitment,
    void **protected_out) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0 ||
        candidate == nullptr || protected_out == nullptr) {
        return false;
    }
    uintptr_t protected_address = 0;
    const uint32_t slot = code_patch_descriptor_slot(
        owner,
        generation,
        reinterpret_cast<uintptr_t>(candidate),
        slot_discriminator);
    const int result = require_il2cpp_commitment &&
            address_is_in_libil2cpp(candidate)
        ? PC_COMPAT_RESOLVE_ADDRESS(
            0,
            0,
            slot,
            0 |
                0,
            reinterpret_cast<uintptr_t>(candidate),
            &protected_address)
        : PC_COMPAT_RESOLVE_CONTINUATION(
            0,
            0,
            slot,
            reinterpret_cast<uintptr_t>(candidate),
            &protected_address);
    if (result != 1 || protected_address != reinterpret_cast<uintptr_t>(candidate))
        return false;
    *protected_out = reinterpret_cast<void *>(protected_address);
    return true;
}

size_t find_code_patch_page_snapshot(uintptr_t page_base, size_t page_size) {
    for (size_t index = 0; index < g_code_patch_page_snapshots.size(); ++index) {
        const auto &snapshot = g_code_patch_page_snapshots[index];
        if (snapshot.page_base == page_base && snapshot.page_size == page_size)
            return index;
    }
    return g_code_patch_page_snapshots.size();
}

size_t find_covering_code_patch_page_snapshot(
    uintptr_t requested_base,
    size_t requested_size) {
    if (requested_size == 0 ||
        requested_base > UINTPTR_MAX - requested_size) {
        return g_code_patch_page_snapshots.size();
    }
    const uintptr_t requested_end = requested_base + requested_size;
    for (size_t index = 0; index < g_code_patch_page_snapshots.size(); ++index) {
        const auto &snapshot = g_code_patch_page_snapshots[index];
        if (snapshot.page_size == 0 ||
            snapshot.page_base > UINTPTR_MAX - snapshot.page_size) {
            continue;
        }
        const uintptr_t snapshot_end = snapshot.page_base + snapshot.page_size;
        if (requested_base >= snapshot.page_base && requested_end <= snapshot_end)
            return index;
    }
    return g_code_patch_page_snapshots.size();
}

bool visit_code_patch_pages(
    void *address,
    uint32_t size,
    const std::function<bool(uintptr_t, size_t)> &visitor) {
    uintptr_t begin = 0;
    uintptr_t end = 0;
    const long raw_page_size = sysconf(_SC_PAGESIZE);
    if (!code_patch_range(address, size, begin, end) || raw_page_size <= 0)
        return false;
    const size_t page_size = static_cast<size_t>(raw_page_size);
    if ((page_size & (page_size - 1U)) != 0)
        return false;
    const uintptr_t first_page = begin & ~(static_cast<uintptr_t>(page_size) - 1U);
    const uintptr_t last_page =
        (end - 1U) & ~(static_cast<uintptr_t>(page_size) - 1U);
    for (uintptr_t page = first_page;; page += page_size) {
        if (!visitor(page, page_size))
            return false;
        if (page == last_page)
            return true;
        if (page > UINTPTR_MAX - page_size)
            return false;
    }
}

bool prepare_code_patch_pages_locked(
    const char *owner,
    uint64_t generation,
    void *address,
    uint32_t size) {
    return visit_code_patch_pages(
        address,
        size,
        [owner, generation, address, size](uintptr_t page, size_t page_size) {
            const size_t existing = find_code_patch_page_snapshot(page, page_size);
            if (existing != g_code_patch_page_snapshots.size()) {
                const auto &snapshot = g_code_patch_page_snapshots[existing];
                return snapshot.expected_current.size() == page_size &&
                    std::memcmp(
                        reinterpret_cast<const void *>(page),
                        snapshot.expected_current.data(),
                        page_size) == 0;
            }

            uintptr_t begin = reinterpret_cast<uintptr_t>(address);
            const uintptr_t candidate = begin > page ? begin : page;
            void *protected_candidate = nullptr;
            if (!protect_modmanager_tool_executable(
                    owner,
                    generation,
                    reinterpret_cast<void *>(candidate),
                    size,
                    true,
                    &protected_candidate) ||
                protected_candidate != reinterpret_cast<void *>(candidate)) {
                LOGE("DobbyCodePatch protected target descriptor failed owner=%s generation=%llu address=%p",
                     owner,
                     static_cast<unsigned long long>(generation),
                     reinterpret_cast<void *>(candidate));
                return false;
            }

            CodePatchPageSnapshot snapshot;
            snapshot.page_base = page;
            snapshot.page_size = page_size;
            const auto *bytes = reinterpret_cast<const uint8_t *>(page);
            snapshot.pristine.assign(bytes, bytes + page_size);
            snapshot.expected_current = snapshot.pristine;
            snapshot.authenticated_pristine = true;
            g_code_patch_page_snapshots.push_back(std::move(snapshot));
            return true;
        });
}

bool authenticate_code_patch_pages_current_locked(
    const char *owner,
    uint64_t generation,
    void *address,
    uint32_t size) {
    return visit_code_patch_pages(
        address,
        size,
        [owner, generation, address, size](uintptr_t page, size_t) {
            const uintptr_t begin = reinterpret_cast<uintptr_t>(address);
            const uintptr_t candidate = begin > page ? begin : page;
            void *protected_candidate = nullptr;
            return protect_modmanager_tool_executable(
                       owner,
                       generation,
                       reinterpret_cast<void *>(candidate),
                       size ^ UINT32_C(0x80000000),
                       false,
                       &protected_candidate) &&
                protected_candidate == reinterpret_cast<void *>(candidate);
        });
}

bool verify_code_patch_pages_locked(void *address, uint32_t size) {
    return visit_code_patch_pages(
        address,
        size,
        [](uintptr_t page, size_t page_size) {
            const size_t index = find_code_patch_page_snapshot(page, page_size);
            if (index == g_code_patch_page_snapshots.size())
                return false;
            const auto &snapshot = g_code_patch_page_snapshots[index];
            return snapshot.expected_current.size() == page_size &&
                std::memcmp(
                    reinterpret_cast<const void *>(page),
                    snapshot.expected_current.data(),
                    page_size) == 0;
        });
}

bool commit_code_patch_pages_locked(void *address, uint32_t size) {
    return visit_code_patch_pages(
        address,
        size,
        [](uintptr_t page, size_t page_size) {
            const size_t index = find_code_patch_page_snapshot(page, page_size);
            if (index == g_code_patch_page_snapshots.size())
                return false;
            auto &snapshot = g_code_patch_page_snapshots[index];
            const auto *bytes = reinterpret_cast<const uint8_t *>(page);
            snapshot.expected_current.assign(bytes, bytes + page_size);
            return true;
        });
}

int apply_code_patch_target_locked(CodePatchTarget &target) {
    const std::vector<uint8_t> *effective = &target.original;
    for (auto layer = target.layers.rbegin(); layer != target.layers.rend(); ++layer) {
        if (layer->enabled && !layer->retired) {
            effective = &layer->bytes;
            break;
        }
    }
    if (!verify_code_patch_pages_locked(target.address, target.size)) {
        LOGE("DobbyCodePatch target page changed outside generation registry address=%p size=%u",
             target.address,
             target.size);
        return -6;
    }
    const int result = DobbyCodePatch(
        target.address,
        const_cast<uint8_t *>(effective->data()),
        target.size);
    if (result != 0)
        return result;
    return commit_code_patch_pages_locked(target.address, target.size)
        ? 0
        : -7;
}

bool rollback_code_patch_states_locked(
    const std::vector<CodePatchLayerState> &states,
    const std::vector<bool> &affected_targets) {
    for (const auto &state : states) {
        g_code_patch_targets[state.target_index]
            .layers[state.layer_index]
            .enabled = state.enabled;
    }
    bool restored = true;
    for (size_t index = 0; index < affected_targets.size(); ++index) {
        if (affected_targets[index] &&
            apply_code_patch_target_locked(g_code_patch_targets[index]) != 0) {
            restored = false;
        }
    }
    return restored;
}

int install_code_patch_generation(
    const char *owner,
    uint64_t generation,
    void *address,
    const uint8_t *buffer,
    uint32_t buffer_size) {
    uintptr_t requested_begin = 0;
    uintptr_t requested_end = 0;
    if (owner == nullptr || owner[0] == '\0' || generation == 0 ||
        buffer == nullptr ||
        !code_patch_range(address, buffer_size, requested_begin, requested_end)) {
        return -1;
    }

    starray::native_patch::Transaction patch_transaction;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    size_t target_index = g_code_patch_targets.size();
    for (size_t index = 0; index < g_code_patch_targets.size(); ++index) {
        auto &candidate = g_code_patch_targets[index];
        const uintptr_t candidate_begin =
            reinterpret_cast<uintptr_t>(candidate.address);
        const uintptr_t candidate_end = candidate_begin + candidate.size;
        if (candidate.address == address && candidate.size == buffer_size) {
            target_index = index;
            break;
        }
        if (code_patch_ranges_overlap(
                requested_begin,
                requested_end,
                candidate_begin,
                candidate_end)) {
            LOGE("DobbyCodePatch partial overlap rejected address=%p size=%u existing=%p/%u",
                 address,
                 buffer_size,
                 candidate.address,
                 candidate.size);
            return -3;
        }
    }
    starray::native_patch::ReservationToken patch_reservation;
    std::string reservation_error;
    const auto reservation_result = patch_transaction.Reserve(
        starray::native_patch::Kind::CodePatch,
        "generation-code-patch-registry",
        owner,
        generation,
        address,
        buffer_size,
        patch_reservation,
        reservation_error);
    if (reservation_result != starray::native_patch::ReserveResult::Acquired &&
        reservation_result != starray::native_patch::ReserveResult::Reused) {
        LOGE("DobbyCodePatch coordinator rejected owner=%s generation=%llu address=%p size=%u error=%s",
             owner,
             static_cast<unsigned long long>(generation),
             address,
             buffer_size,
             reservation_error.c_str());
        return -8;
    }
    if (!prepare_code_patch_pages_locked(
            owner, generation, address, buffer_size)) {
        return -2;
    }

    bool created_target = false;
    if (target_index == g_code_patch_targets.size()) {
        CodePatchTarget target;
        target.address = address;
        target.size = buffer_size;
        const auto *current = static_cast<const uint8_t *>(address);
        target.original.assign(current, current + buffer_size);
        g_code_patch_targets.push_back(std::move(target));
        target_index = g_code_patch_targets.size() - 1;
        created_target = true;
    }

    auto &target = g_code_patch_targets[target_index];
    for (auto &layer : target.layers) {
        if (!layer.retired && layer.generation == generation &&
            layer.owner == owner && layer.bytes.size() == buffer_size &&
            std::memcmp(layer.bytes.data(), buffer, buffer_size) == 0) {
            if (!authenticate_code_patch_pages_current_locked(
                    owner, generation, target.address, target.size)) {
                LOGE("DobbyCodePatch generation reuse descriptor rejected owner=%s generation=%llu address=%p size=%u",
                     owner,
                     static_cast<unsigned long long>(generation),
                     target.address,
                     target.size);
                return -9;
            }
            const bool previous_enabled = layer.enabled;
            layer.enabled = true;
            const int result = apply_code_patch_target_locked(target);
            if (result != 0) {
                layer.enabled = previous_enabled;
                if (apply_code_patch_target_locked(target) != 0)
                    return -5;
            }
            patch_transaction.Commit(patch_reservation);
            return result;
        }
    }

    if (g_code_patch_sequence == UINT64_MAX)
        return -4;
    CodePatchLayer layer;
    layer.owner = owner;
    layer.generation = generation;
    layer.sequence = ++g_code_patch_sequence;
    layer.bytes.assign(buffer, buffer + buffer_size);
    layer.enabled = true;
    target.layers.push_back(std::move(layer));

    const int result = apply_code_patch_target_locked(target);
    if (result == 0) {
        patch_transaction.Commit(patch_reservation);
        LOGI("DobbyCodePatch generation installed owner=%s generation=%llu address=%p size=%u sequence=%llu",
             owner,
             static_cast<unsigned long long>(generation),
             address,
             buffer_size,
             static_cast<unsigned long long>(g_code_patch_sequence));
        return 0;
    }

    target.layers.pop_back();
    const int rollback_result = apply_code_patch_target_locked(target);
    if (rollback_result == 0 && created_target && target.layers.empty())
        g_code_patch_targets.pop_back();
    if (rollback_result != 0) {
        LOGE("DobbyCodePatch install rollback failed address=%p result=%d rollback=%d",
             address,
             result,
             rollback_result);
        return -5;
    }
    return result;
}

int set_code_patch_generation_enabled(
    const char *owner,
    uint64_t generation,
    bool enabled) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return -1;

    starray::native_patch::Transaction patch_transaction;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    std::vector<CodePatchLayerState> states;
    std::vector<bool> affected_targets(g_code_patch_targets.size(), false);
    for (size_t target_index = 0;
         target_index < g_code_patch_targets.size();
         ++target_index) {
        auto &target = g_code_patch_targets[target_index];
        for (size_t layer_index = 0; layer_index < target.layers.size(); ++layer_index) {
            auto &layer = target.layers[layer_index];
            if (layer.retired || layer.generation != generation ||
                layer.owner != owner || layer.enabled == enabled) {
                continue;
            }
            affected_targets[target_index] = true;
        }
    }

    if (enabled) {
        for (size_t index = 0; index < affected_targets.size(); ++index) {
            if (!affected_targets[index])
                continue;
            const auto &target = g_code_patch_targets[index];
            if (!authenticate_code_patch_pages_current_locked(
                    owner, generation, target.address, target.size)) {
                LOGE("DobbyCodePatch generation enable descriptor rejected owner=%s generation=%llu address=%p size=%u",
                     owner,
                     static_cast<unsigned long long>(generation),
                     target.address,
                     target.size);
                return -4;
            }
        }
    }

    for (size_t target_index = 0;
         target_index < g_code_patch_targets.size();
         ++target_index) {
        if (!affected_targets[target_index])
            continue;
        auto &target = g_code_patch_targets[target_index];
        for (size_t layer_index = 0; layer_index < target.layers.size(); ++layer_index) {
            auto &layer = target.layers[layer_index];
            if (layer.retired || layer.generation != generation ||
                layer.owner != owner || layer.enabled == enabled) {
                continue;
            }
            states.push_back({target_index, layer_index, layer.enabled});
            layer.enabled = enabled;
        }
    }

    for (size_t index = 0; index < affected_targets.size(); ++index) {
        if (!affected_targets[index])
            continue;
        const int result = apply_code_patch_target_locked(g_code_patch_targets[index]);
        if (result != 0) {
            const bool restored = rollback_code_patch_states_locked(
                states, affected_targets);
            LOGE("DobbyCodePatch generation state rollback owner=%s generation=%llu result=%d restored=%d",
                 owner,
                 static_cast<unsigned long long>(generation),
                 result,
                 restored ? 1 : 0);
            return restored ? -2 : -3;
        }
    }
    return static_cast<int>(states.size());
}

int retire_code_patch_generation(
    const char *owner,
    uint64_t generation,
    void *address) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return -1;

    starray::native_patch::Transaction patch_transaction;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    std::vector<CodePatchLayerState> states;
    std::vector<bool> affected_targets(g_code_patch_targets.size(), false);
    for (size_t target_index = 0;
         target_index < g_code_patch_targets.size();
         ++target_index) {
        auto &target = g_code_patch_targets[target_index];
        if (address != nullptr && target.address != address)
            continue;
        for (size_t layer_index = 0; layer_index < target.layers.size(); ++layer_index) {
            auto &layer = target.layers[layer_index];
            if (layer.retired || layer.generation != generation ||
                layer.owner != owner) {
                continue;
            }
            states.push_back({target_index, layer_index, layer.enabled});
            layer.enabled = false;
            affected_targets[target_index] = true;
        }
    }

    for (size_t index = 0; index < affected_targets.size(); ++index) {
        if (!affected_targets[index])
            continue;
        const int result = apply_code_patch_target_locked(g_code_patch_targets[index]);
        if (result != 0) {
            const bool restored = rollback_code_patch_states_locked(
                states, affected_targets);
            LOGE("DobbyCodePatch retirement rollback owner=%s generation=%llu result=%d restored=%d",
                 owner,
                 static_cast<unsigned long long>(generation),
                 result,
                 restored ? 1 : 0);
            return restored ? -2 : -3;
        }
    }

    for (const auto &state : states) {
        auto &layer = g_code_patch_targets[state.target_index]
            .layers[state.layer_index];
        layer.retired = true;
        layer.bytes.clear();
    }
    return static_cast<int>(states.size());
}

int count_code_patch_generation(const char *owner, uint64_t generation) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return 0;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    int count = 0;
    for (const auto &target : g_code_patch_targets) {
        for (const auto &layer : target.layers) {
            if (!layer.retired && layer.generation == generation &&
                layer.owner == owner) {
                ++count;
            }
        }
    }
    return count;
}

uint32_t symbol_descriptor_slot(
    const char *owner,
    uint64_t generation,
    const char *image_name,
    const char *symbol_name) {
    uint32_t hash = UINT32_C(2166136261);
    const auto mix = [&hash](const char *text) {
        for (const auto *cursor = reinterpret_cast<const uint8_t *>(text);
             *cursor != 0;
             ++cursor) {
            hash ^= *cursor;
            hash *= UINT32_C(16777619);
        }
        hash ^= UINT8_C(0xFF);
        hash *= UINT32_C(16777619);
    };
    mix(owner);
    for (size_t index = 0; index < sizeof(generation); ++index) {
        hash ^= static_cast<uint8_t>(generation >> (index * 8U));
        hash *= UINT32_C(16777619);
    }
    mix(image_name);
    mix(symbol_name);
    return UINT32_C(0x40000000) | (hash & UINT32_C(0x3FFFFFFF));
}

bool is_libil2cpp_image(const char *image_name) {
    if (image_name == nullptr)
        return false;
    const char *base_name = std::strrchr(image_name, '/');
    base_name = base_name == nullptr ? image_name : base_name + 1;
    return std::strcmp(base_name, "libil2cpp.so") == 0;
}

void *resolve_symbol_generation(
    const char *owner,
    uint64_t generation,
    const char *image_name,
    const char *symbol_name) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0 ||
        image_name == nullptr || image_name[0] == '\0' ||
        symbol_name == nullptr || symbol_name[0] == '\0') {
        return nullptr;
    }

    void *candidate = DobbySymbolResolver(image_name, symbol_name);
    if (candidate == nullptr || !is_libil2cpp_image(image_name))
        return candidate;

    uintptr_t protected_address = 0;
    const uint32_t slot = symbol_descriptor_slot(
        owner, generation, image_name, symbol_name);
    if (!PC_COMPAT_RESOLVE_ADDRESS(
            0,
            0,
            slot,
            0 |
                0,
            reinterpret_cast<uintptr_t>(candidate),
            &protected_address) ||
        protected_address != reinterpret_cast<uintptr_t>(candidate)) {
        LOGE("DobbySymbolResolver protected descriptor failed owner=%s generation=%llu symbol=%s",
             owner,
             static_cast<unsigned long long>(generation),
             symbol_name);
        return nullptr;
    }
    return reinterpret_cast<void *>(protected_address);
}

} // namespace

namespace starray::code_patch {

bool PrepareExternalWrite(void *address, size_t size) {
    if (address == nullptr || size == 0 || size > UINT32_MAX)
        return false;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    return visit_code_patch_pages(
        address,
        static_cast<uint32_t>(size),
        [](uintptr_t page, size_t page_size) {
            const size_t index = find_code_patch_page_snapshot(page, page_size);
            if (index == g_code_patch_page_snapshots.size())
                return true;
            const auto &snapshot = g_code_patch_page_snapshots[index];
            return snapshot.expected_current.size() == page_size &&
                std::memcmp(
                    reinterpret_cast<const void *>(page),
                    snapshot.expected_current.data(),
                    page_size) == 0;
        });
}

void CommitExternalWrite(void *address, size_t size) {
    if (address == nullptr || size == 0 || size > UINT32_MAX)
        return;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    (void)visit_code_patch_pages(
        address,
        static_cast<uint32_t>(size),
        [](uintptr_t page, size_t page_size) {
            const size_t index = find_code_patch_page_snapshot(page, page_size);
            if (index == g_code_patch_page_snapshots.size())
                return true;
            auto &snapshot = g_code_patch_page_snapshots[index];
            const auto *bytes = reinterpret_cast<const uint8_t *>(page);
            snapshot.expected_current.assign(bytes, bytes + page_size);
            return true;
        });
}

bool PrepareHookBrokerWrite(
    void *address,
    size_t size,
    bool authenticate_pristine) {
    if (address == nullptr || size == 0 || size > UINT32_MAX)
        return false;
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    return visit_code_patch_pages(
        address,
        static_cast<uint32_t>(size),
        [authenticate_pristine](uintptr_t page, size_t page_size) {
            const size_t index = find_code_patch_page_snapshot(page, page_size);
            if (index != g_code_patch_page_snapshots.size()) {
                auto &snapshot = g_code_patch_page_snapshots[index];
                if (snapshot.expected_current.size() != page_size ||
                    std::memcmp(
                        reinterpret_cast<const void *>(page),
                        snapshot.expected_current.data(),
                        page_size) != 0) {
                    return false;
                }
                snapshot.authenticated_pristine =
                    snapshot.authenticated_pristine || authenticate_pristine;
                return true;
            }
            if (!authenticate_pristine)
                return true;

            CodePatchPageSnapshot snapshot;
            snapshot.page_base = page;
            snapshot.page_size = page_size;
            const auto *bytes = reinterpret_cast<const uint8_t *>(page);
            snapshot.pristine.assign(bytes, bytes + page_size);
            snapshot.expected_current = snapshot.pristine;
            snapshot.authenticated_pristine = true;
            g_code_patch_page_snapshots.push_back(std::move(snapshot));
            return true;
        });
}

void CommitHookBrokerWrite(void *address, size_t size) {
    CommitExternalWrite(address, size);
}

int CopyAuthenticatedPristinePage(
    const void *page_base,
    size_t page_size,
    void *output,
    size_t output_size) {
    if (page_base == nullptr || page_size == 0 || output == nullptr ||
        output_size != page_size) {
        return MODMANAGER_PRISTINE_PAGE_INVALID_ARGUMENT;
    }
    std::lock_guard<std::mutex> guard(g_code_patch_lock);
    const uintptr_t requested_base = reinterpret_cast<uintptr_t>(page_base);
    const size_t index = find_covering_code_patch_page_snapshot(
        requested_base, page_size);
    if (index == g_code_patch_page_snapshots.size())
        return MODMANAGER_PRISTINE_PAGE_NOT_FOUND;
    const auto &snapshot = g_code_patch_page_snapshots[index];
    const size_t requested_offset =
        static_cast<size_t>(requested_base - snapshot.page_base);
    if (!snapshot.authenticated_pristine)
        return MODMANAGER_PRISTINE_PAGE_NOT_AUTHENTICATED;
    if (snapshot.pristine.size() != snapshot.page_size)
        return MODMANAGER_PRISTINE_PAGE_PRISTINE_SIZE_MISMATCH;
    if (snapshot.expected_current.size() != snapshot.page_size)
        return MODMANAGER_PRISTINE_PAGE_EXPECTED_SIZE_MISMATCH;
    if (std::memcmp(
            reinterpret_cast<const void *>(snapshot.page_base),
            snapshot.expected_current.data(),
            snapshot.page_size) != 0) {
        return MODMANAGER_PRISTINE_PAGE_CURRENT_MISMATCH;
    }
    std::memcpy(
        output,
        snapshot.pristine.data() + requested_offset,
        page_size);
    return MODMANAGER_PRISTINE_PAGE_COPIED;
}

} // namespace starray::code_patch

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
    if (address == nullptr || pre_handler == nullptr)
        return -1;
    if (address_is_in_libil2cpp(address)) {
        LOGE("unscoped IL2CPP instrument rejected address=%p", address);
        return -3;
    }
    starray::native_patch::Transaction patch_transaction;
    starray::native_patch::ReservationToken patch_reservation;
    std::string reservation_error;
    const auto reservation_result = patch_transaction.Reserve(
        starray::native_patch::Kind::Instrument,
        "legacy-dobby-instrument",
        "host:legacy-instrument",
        0,
        address,
        starray::native_patch::kConservativeDobbyArm64PatchSize,
        patch_reservation,
        reservation_error);
    if (reservation_result != starray::native_patch::ReserveResult::Acquired ||
        !starray::code_patch::PrepareExternalWrite(
            address,
            starray::native_patch::kConservativeDobbyArm64PatchSize)) {
        LOGE("DobbyInstrument legacy coordinator rejected address=%p error=%s",
             address,
             reservation_error.c_str());
        return -2;
    }
    LOGI("DobbyInstrument at %p, handler=%p", address, pre_handler);
    const int result = DobbyInstrument(
        address, (dobby_instrument_callback_t)pre_handler);
    if (result == 0) {
        patch_transaction.Commit(patch_reservation);
        starray::code_patch::CommitExternalWrite(
            address,
            starray::native_patch::kConservativeDobbyArm64PatchSize);
    }
    return result;
}

int modmanager_dobby_instrument_generation(
    const char *owner,
    uint64_t generation,
    void *address,
    void *pre_handler) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0 ||
        address == nullptr || pre_handler == nullptr) {
        return -1;
    }

    starray::native_patch::Transaction patch_transaction;
    std::lock_guard<std::mutex> guard(g_instrument_lock);
    size_t free_slot = kInstrumentSlotCount;
    for (size_t index = 0; index < g_instrument_slots.size(); ++index) {
        auto &slot = g_instrument_slots[index];
        if (slot.target == nullptr && free_slot == kInstrumentSlotCount)
            free_slot = index;
        if (slot.target != address) {
            continue;
        }
        if (slot.retired.load(std::memory_order_acquire) != 0) {
            LOGE("DobbyInstrument target is permanently retired address=%p slot=%zu",
                 address,
                 index);
            return -5;
        }
        if (instrument_slot_matches(slot, owner, generation) &&
            slot.handler.load(std::memory_order_acquire) ==
                reinterpret_cast<uintptr_t>(pre_handler)) {
            void *protected_handler = nullptr;
            if (!protect_modmanager_tool_executable(
                    owner,
                    generation,
                    pre_handler,
                    UINT32_C(0x00FFFF12),
                    false,
                    &protected_handler) ||
                protected_handler != pre_handler) {
                LOGE("DobbyInstrument generation reuse descriptor rejected owner=%s generation=%llu target=%p",
                     owner,
                     static_cast<unsigned long long>(generation),
                     address);
                return -7;
            }
            slot.enabled.store(1, std::memory_order_release);
            return 0;
        }
        LOGE("DobbyInstrument target already owned address=%p", address);
        return -3;
    }
    if (free_slot == kInstrumentSlotCount) {
        LOGE("DobbyInstrument generation registry exhausted");
        return -4;
    }

    void *protected_target = nullptr;
    void *protected_handler = nullptr;
    if (!protect_modmanager_tool_executable(
            owner,
            generation,
            address,
            UINT32_C(0x00FFFF01),
            true,
            &protected_target) ||
        !protect_modmanager_tool_executable(
            owner,
            generation,
            pre_handler,
            UINT32_C(0x00FFFF02),
            false,
            &protected_handler)) {
        LOGE("DobbyInstrument generation descriptor rejected owner=%s generation=%llu target=%p handler=%p",
             owner,
             static_cast<unsigned long long>(generation),
             address,
             pre_handler);
        return -7;
    }
    address = protected_target;
    pre_handler = protected_handler;

    starray::native_patch::ReservationToken patch_reservation;
    std::string reservation_error;
    const auto reservation_result = patch_transaction.Reserve(
        starray::native_patch::Kind::Instrument,
        "generation-instrument-registry",
        owner,
        generation,
        address,
        starray::native_patch::kConservativeDobbyArm64PatchSize,
        patch_reservation,
        reservation_error);
    if (reservation_result != starray::native_patch::ReserveResult::Acquired ||
        !starray::code_patch::PrepareHookBrokerWrite(
            address,
            starray::native_patch::kConservativeDobbyArm64PatchSize,
            true)) {
        LOGE("DobbyInstrument generation coordinator rejected owner=%s generation=%llu address=%p error=%s",
             owner,
             static_cast<unsigned long long>(generation),
             address,
             reservation_error.c_str());
        return -6;
    }

    auto &slot = g_instrument_slots[free_slot];
    slot.owner = owner;
    slot.generation = generation;
    slot.target = address;
    slot.handler.store(
        reinterpret_cast<uintptr_t>(pre_handler), std::memory_order_release);
    slot.enabled.store(0, std::memory_order_release);
    slot.retired.store(0, std::memory_order_release);
    slot.active_callbacks.store(0, std::memory_order_release);

    const int result = DobbyInstrument(address, kInstrumentDispatchers[free_slot]);
    if (result != 0) {
        slot.handler.store(0, std::memory_order_release);
        slot.target = nullptr;
        slot.generation = 0;
        slot.owner.clear();
        return result;
    }
    patch_transaction.Commit(patch_reservation);
    starray::code_patch::CommitHookBrokerWrite(
        address,
        starray::native_patch::kConservativeDobbyArm64PatchSize);
    slot.enabled.store(1, std::memory_order_release);
    LOGI("DobbyInstrument generation installed owner=%s generation=%llu address=%p slot=%zu",
         owner,
         static_cast<unsigned long long>(generation),
         address,
         free_slot);
    return 0;
}

int modmanager_dobby_set_instrument_generation_enabled(
    const char *owner,
    uint64_t generation,
    int enabled) {
    return set_instrument_generation_enabled(owner, generation, enabled != 0);
}

int modmanager_dobby_retire_instrument_generation_target(
    const char *owner,
    uint64_t generation,
    void *target) {
    if (target == nullptr)
        return -1;
    return retire_instrument_generation(owner, generation, target);
}

int modmanager_dobby_retire_instrument_generation(
    const char *owner,
    uint64_t generation) {
    return retire_instrument_generation(owner, generation, nullptr);
}

int modmanager_dobby_get_instrument_generation_retained_count(
    const char *owner,
    uint64_t generation) {
    return count_instrument_generation(owner, generation);
}

int modmanager_dobby_code_patch_generation(
    const char *owner,
    uint64_t generation,
    void *address,
    const uint8_t *buffer,
    uint32_t buffer_size) {
    return install_code_patch_generation(
        owner, generation, address, buffer, buffer_size);
}

int modmanager_dobby_set_code_patch_generation_enabled(
    const char *owner,
    uint64_t generation,
    int enabled) {
    return set_code_patch_generation_enabled(owner, generation, enabled != 0);
}

int modmanager_dobby_retire_code_patch_generation_target(
    const char *owner,
    uint64_t generation,
    void *target) {
    if (target == nullptr)
        return -1;
    return retire_code_patch_generation(owner, generation, target);
}

int modmanager_dobby_retire_code_patch_generation(
    const char *owner,
    uint64_t generation) {
    return retire_code_patch_generation(owner, generation, nullptr);
}

int modmanager_dobby_get_code_patch_generation_retained_count(
    const char *owner,
    uint64_t generation) {
    return count_code_patch_generation(owner, generation);
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

void *modmanager_dobby_symbol_resolver_generation(
    const char *owner,
    uint64_t generation,
    const char *image_name,
    const char *symbol_name) {
    void *address = resolve_symbol_generation(
        owner, generation, image_name, symbol_name);
    LOGI("DobbySymbolResolver generation owner=%s generation=%llu image=%s symbol=%s address=%p",
         owner != nullptr ? owner : "<null>",
         static_cast<unsigned long long>(generation),
         image_name != nullptr ? image_name : "<null>",
         symbol_name != nullptr ? symbol_name : "<null>",
         address);
    return address;
}

void *modmanager_gl_get_proc_address(const char *symbol_name) {
    if (symbol_name == nullptr || *symbol_name == '\0')
        return nullptr;

    void *address = dlsym(RTLD_DEFAULT, symbol_name);
    if (address != nullptr)
        return address;

    using EglGetProcAddress = void *(*)(const char *);
    static EglGetProcAddress egl_get_proc_address =
        reinterpret_cast<EglGetProcAddress>(
            dlsym(RTLD_DEFAULT, "eglGetProcAddress"));
    return egl_get_proc_address != nullptr
        ? egl_get_proc_address(symbol_name)
        : nullptr;
}

/**
 * DobbyCodePatch — 内存代码补丁。
 * @param address      目标地址
 * @param buffer       补丁数据
 * @param buffer_size  补丁数据大小
 * @return 0 成功
 */
int modmanager_dobby_code_patch(void *address, const uint8_t *buffer, uint32_t buffer_size) {
    if (address == nullptr || buffer == nullptr || buffer_size == 0)
        return -1;
    if (address_is_in_libil2cpp(address)) {
        LOGE("unscoped IL2CPP code patch rejected address=%p size=%u",
             address, buffer_size);
        return -3;
    }
    starray::native_patch::Transaction patch_transaction;
    starray::native_patch::ReservationToken patch_reservation;
    std::string reservation_error;
    const auto reservation_result = patch_transaction.Reserve(
        starray::native_patch::Kind::CodePatch,
        "legacy-dobby-code-patch",
        "host:legacy-code-patch",
        0,
        address,
        buffer_size,
        patch_reservation,
        reservation_error);
    if ((reservation_result != starray::native_patch::ReserveResult::Acquired &&
         reservation_result != starray::native_patch::ReserveResult::Reused) ||
        !starray::code_patch::PrepareExternalWrite(address, buffer_size)) {
        LOGE("DobbyCodePatch legacy coordinator rejected address=%p size=%u error=%s",
             address,
             buffer_size,
             reservation_error.c_str());
        return -2;
    }
    LOGI("DobbyCodePatch at %p, size=%u", address, buffer_size);
    const int result = DobbyCodePatch(
        address, const_cast<uint8_t *>(buffer), buffer_size);
    if (result == 0) {
        patch_transaction.Commit(patch_reservation);
        starray::code_patch::CommitExternalWrite(address, buffer_size);
    }
    return result;
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

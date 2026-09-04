#include "native_mod_operation_registry.h"

#include "modmanager_native_operation_client.h"

#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <unordered_map>

namespace {

constexpr size_t kOperationSlotCount = 256;
constexpr size_t kMaximumOwnerLength = 192;
constexpr size_t kMaximumNameLength = 160;

static_assert(sizeof(modmanager_native_operation_token_v1) == 24);
static_assert(offsetof(modmanager_native_operation_token_v1, abi_version) == 0);
static_assert(offsetof(modmanager_native_operation_token_v1, slot) == 4);
static_assert(offsetof(modmanager_native_operation_token_v1, operation_id) == 8);
static_assert(offsetof(modmanager_native_operation_token_v1, cookie) == 16);

enum class GenerationLifecycle : uint8_t {
    Active,
    Retiring,
    Retired,
};

struct GenerationKey {
    std::string owner;
    uint64_t generation = 0;

    bool operator==(const GenerationKey &other) const {
        return generation == other.generation && owner == other.owner;
    }
};

struct GenerationKeyHash {
    size_t operator()(const GenerationKey &key) const {
        const size_t owner_hash = std::hash<std::string>{}(key.owner);
        const size_t generation_hash = std::hash<uint64_t>{}(key.generation);
        return owner_hash ^ (generation_hash + static_cast<size_t>(0x9e3779b9u) +
            (owner_hash << 6u) + (owner_hash >> 2u));
    }
};

struct OperationSlot {
    std::atomic<uint32_t> active{0};
    std::atomic<uint32_t> cancellation_requested{0};
    std::atomic<uint64_t> operation_id{0};
    std::atomic<uint64_t> cookie{0};
    std::string owner;
    std::string name;
    uint64_t generation = 0;
};

static_assert(std::atomic<uint32_t>::is_always_lock_free);
static_assert(std::atomic<uint64_t>::is_always_lock_free);

std::mutex g_operation_lock;
std::condition_variable g_operation_changed;
std::array<OperationSlot, kOperationSlotCount> g_operation_slots;
std::unordered_map<GenerationKey, GenerationLifecycle, GenerationKeyHash>
    g_generation_lifecycle;
uint64_t g_next_operation_id = 0;

size_t bounded_string_length(const char *value, size_t limit) {
    size_t length = 0;
    while (length < limit && value[length] != '\0')
        ++length;
    return length;
}

bool valid_identity(const char *owner, uint64_t generation) {
    if (owner == nullptr || owner[0] == '\0' || generation == 0)
        return false;
    return bounded_string_length(owner, kMaximumOwnerLength + 1u) <=
        kMaximumOwnerLength;
}

std::string normalize_name(const char *name) {
    if (name == nullptr || name[0] == '\0')
        return "native-operation";
    const size_t length = bounded_string_length(name, kMaximumNameLength + 1u);
    std::string value(name, length > kMaximumNameLength ? kMaximumNameLength : length);
    for (char &character : value) {
        if (character == '\r' || character == '\n')
            character = ' ';
    }
    return value;
}

uint64_t mix64(uint64_t value) {
    value ^= value >> 30u;
    value *= UINT64_C(0xbf58476d1ce4e5b9);
    value ^= value >> 27u;
    value *= UINT64_C(0x94d049bb133111eb);
    return value ^ (value >> 31u);
}

uint64_t process_secret() {
    static const uint64_t value = [] {
        uint64_t seed = static_cast<uint64_t>(
            std::chrono::high_resolution_clock::now().time_since_epoch().count());
        seed ^= static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&g_operation_slots));
        seed ^= static_cast<uint64_t>(
            std::hash<std::thread::id>{}(std::this_thread::get_id()));
        try {
            std::random_device random;
            seed ^= static_cast<uint64_t>(random()) << 32u;
            seed ^= static_cast<uint64_t>(random());
        } catch (...) {
        }
        const uint64_t mixed = mix64(seed);
        return mixed == 0 ? UINT64_C(0x6d6f646f70657231) : mixed;
    }();
    return value;
}

uint64_t make_cookie(
    uint64_t operation_id,
    size_t slot,
    const std::string &owner,
    uint64_t generation) {
    uint64_t value = process_secret() ^ operation_id ^
        (static_cast<uint64_t>(slot) << 48u) ^ generation;
    value ^= static_cast<uint64_t>(std::hash<std::string>{}(owner));
    const uint64_t cookie = mix64(value);
    return cookie == 0 ? UINT64_C(1) : cookie;
}

bool slot_matches_generation(
    const OperationSlot &slot,
    const char *owner,
    uint64_t generation) {
    return slot.active.load(std::memory_order_acquire) != 0 &&
        slot.generation == generation && slot.owner == owner;
}

int active_count_locked(const char *owner, uint64_t generation) {
    int count = 0;
    for (const OperationSlot &slot : g_operation_slots) {
        if (slot_matches_generation(slot, owner, generation))
            ++count;
    }
    return count;
}

bool token_shape_valid(const modmanager_native_operation_token_v1 *token) {
    return token != nullptr &&
        token->abi_version == MODMANAGER_NATIVE_OPERATION_TOKEN_ABI_V1 &&
        token->slot < kOperationSlotCount && token->operation_id != 0 &&
        token->cookie != 0;
}

} // namespace

extern "C" {

int modmanager_native_operation_begin_v1(
    const char *owner,
    uint64_t generation,
    const char *name,
    modmanager_native_operation_token_v1 *token_out) {
    if (!valid_identity(owner, generation) || token_out == nullptr)
        return 0;
    *token_out = {};

    std::lock_guard<std::mutex> guard(g_operation_lock);
    const auto lifecycle = g_generation_lifecycle.find(GenerationKey{owner, generation});
    if (lifecycle == g_generation_lifecycle.end() ||
        lifecycle->second != GenerationLifecycle::Active ||
        g_next_operation_id == UINT64_MAX) {
        return 0;
    }

    size_t slot_index = kOperationSlotCount;
    for (size_t index = 0; index < g_operation_slots.size(); ++index) {
        if (g_operation_slots[index].active.load(std::memory_order_acquire) == 0) {
            slot_index = index;
            break;
        }
    }
    if (slot_index == kOperationSlotCount)
        return 0;

    OperationSlot &slot = g_operation_slots[slot_index];
    const uint64_t operation_id = ++g_next_operation_id;
    const uint64_t cookie = make_cookie(
        operation_id, slot_index, owner, generation);
    slot.owner = owner;
    slot.name = normalize_name(name);
    slot.generation = generation;
    slot.operation_id.store(operation_id, std::memory_order_relaxed);
    slot.cookie.store(cookie, std::memory_order_relaxed);
    slot.cancellation_requested.store(0, std::memory_order_relaxed);
    slot.active.store(1, std::memory_order_release);

    token_out->abi_version = MODMANAGER_NATIVE_OPERATION_TOKEN_ABI_V1;
    token_out->slot = static_cast<uint32_t>(slot_index);
    token_out->operation_id = operation_id;
    token_out->cookie = cookie;
    return 1;
}

int modmanager_native_operation_is_cancellation_requested_v1(
    const modmanager_native_operation_token_v1 *token) {
    if (!token_shape_valid(token))
        return -1;
    const OperationSlot &slot = g_operation_slots[token->slot];
    if (slot.active.load(std::memory_order_acquire) == 0 ||
        slot.operation_id.load(std::memory_order_acquire) != token->operation_id ||
        slot.cookie.load(std::memory_order_acquire) != token->cookie) {
        return -1;
    }
    const int requested = slot.cancellation_requested.load(
        std::memory_order_acquire) != 0 ? 1 : 0;
    if (slot.active.load(std::memory_order_acquire) == 0 ||
        slot.operation_id.load(std::memory_order_acquire) != token->operation_id ||
        slot.cookie.load(std::memory_order_acquire) != token->cookie) {
        return -1;
    }
    return requested;
}

int modmanager_native_operation_end_v1(
    const modmanager_native_operation_token_v1 *token) {
    if (!token_shape_valid(token))
        return 0;
    std::lock_guard<std::mutex> guard(g_operation_lock);
    OperationSlot &slot = g_operation_slots[token->slot];
    if (slot.active.load(std::memory_order_acquire) == 0 ||
        slot.operation_id.load(std::memory_order_relaxed) != token->operation_id ||
        slot.cookie.load(std::memory_order_relaxed) != token->cookie) {
        return 0;
    }
    slot.cancellation_requested.store(1, std::memory_order_release);
    slot.active.store(0, std::memory_order_release);
    slot.owner.clear();
    slot.name.clear();
    slot.generation = 0;
    g_operation_changed.notify_all();
    return 1;
}

int modmanager_native_operation_host_open_generation(
    const char *owner,
    uint64_t generation) {
    if (!valid_identity(owner, generation))
        return 0;
    std::lock_guard<std::mutex> guard(g_operation_lock);
    const GenerationKey key{owner, generation};
    const auto existing = g_generation_lifecycle.find(key);
    if (existing == g_generation_lifecycle.end()) {
        g_generation_lifecycle.emplace(key, GenerationLifecycle::Active);
        return 1;
    }
    return existing->second == GenerationLifecycle::Active ? 1 : 0;
}

int modmanager_native_operation_host_cancel_generation_and_wait(
    const char *owner,
    uint64_t generation,
    uint32_t timeout_ms) {
    if (!valid_identity(owner, generation))
        return 0;
    std::unique_lock<std::mutex> guard(g_operation_lock);
    const GenerationKey key{owner, generation};
    const auto lifecycle = g_generation_lifecycle.find(key);
    if (lifecycle == g_generation_lifecycle.end())
        return 1;
    if (lifecycle->second == GenerationLifecycle::Retired)
        return active_count_locked(owner, generation) == 0 ? 1 : 0;
    lifecycle->second = GenerationLifecycle::Retiring;
    for (OperationSlot &slot : g_operation_slots) {
        if (slot_matches_generation(slot, owner, generation))
            slot.cancellation_requested.store(1, std::memory_order_release);
    }

    const auto quiesced = [&] {
        return active_count_locked(owner, generation) == 0;
    };
    if (quiesced())
        return 1;
    return g_operation_changed.wait_for(
        guard,
        std::chrono::milliseconds(timeout_ms),
        quiesced) ? 1 : 0;
}

int modmanager_native_operation_host_resume_generation(
    const char *owner,
    uint64_t generation) {
    if (!valid_identity(owner, generation))
        return 0;
    std::lock_guard<std::mutex> guard(g_operation_lock);
    const auto lifecycle = g_generation_lifecycle.find(GenerationKey{owner, generation});
    if (lifecycle == g_generation_lifecycle.end() ||
        lifecycle->second == GenerationLifecycle::Retired) {
        return 0;
    }
    lifecycle->second = GenerationLifecycle::Active;
    return 1;
}

int modmanager_native_operation_host_retire_generation(
    const char *owner,
    uint64_t generation) {
    if (!valid_identity(owner, generation))
        return 0;
    std::lock_guard<std::mutex> guard(g_operation_lock);
    const auto lifecycle = g_generation_lifecycle.find(GenerationKey{owner, generation});
    if (lifecycle == g_generation_lifecycle.end())
        return 1;
    if (active_count_locked(owner, generation) != 0 ||
        lifecycle->second == GenerationLifecycle::Active) {
        return 0;
    }
    lifecycle->second = GenerationLifecycle::Retired;
    return 1;
}

int modmanager_native_operation_host_get_active_count(
    const char *owner,
    uint64_t generation) {
    if (!valid_identity(owner, generation))
        return -1;
    std::lock_guard<std::mutex> guard(g_operation_lock);
    return active_count_locked(owner, generation);
}

} // extern "C"

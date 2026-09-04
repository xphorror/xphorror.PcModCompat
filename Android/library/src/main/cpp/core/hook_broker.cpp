#include "hook_broker.h"
#include "dobby_hook_internal.h"
#include "pccompat_open_runtime.h"
#include "native_patch_coordinator.h"

#include <android/log.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <dlfcn.h>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <utility>
#include <vector>

#include <sys/mman.h>
#include <unistd.h>

#include <dobby.h>

#define LOG_TAG "StArray.HookBroker"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace {

class ExecutableMapping {
public:
    ExecutableMapping() = default;
    ExecutableMapping(const ExecutableMapping &) = delete;
    ExecutableMapping &operator=(const ExecutableMapping &) = delete;

    ~ExecutableMapping() {
        if (address_ != nullptr)
            munmap(address_, size_);
    }

    bool allocate(size_t payload_size, std::string &error) {
        const long raw_page_size = sysconf(_SC_PAGESIZE);
        if (raw_page_size <= 0) {
            error = "cannot resolve system page size";
            return false;
        }
        const size_t page_size = static_cast<size_t>(raw_page_size);
        const size_t page_count = (payload_size + page_size - 1u) / page_size;
        size_ = page_count * page_size;
        address_ = mmap(
            nullptr,
            size_,
            PROT_READ | PROT_WRITE,
            MAP_PRIVATE | MAP_ANONYMOUS,
            -1,
            0);
        if (address_ == MAP_FAILED) {
            address_ = nullptr;
            size_ = 0;
            error = "mmap failed for hook gateway";
            return false;
        }
        return true;
    }

    bool seal(size_t payload_size, std::string &error) {
        if (address_ == nullptr || payload_size > size_) {
            error = "invalid executable mapping payload";
            return false;
        }
        __builtin___clear_cache(
            reinterpret_cast<char *>(address_),
            reinterpret_cast<char *>(address_) + payload_size);
        if (mprotect(address_, size_, PROT_READ | PROT_EXEC) != 0) {
            error = "mprotect failed for hook gateway";
            return false;
        }
        return true;
    }

    void *address() const { return address_; }

private:
    void *address_ = nullptr;
    size_t size_ = 0;
};

struct HookLayer {
    std::string owner;
    uint64_t generation = 0;
    void *replacement = nullptr;
    int compatibility_kind = MODMANAGER_HOOK_ABI_NONE;
    void *continuation = nullptr;
    void *entry = nullptr;
    std::atomic<void *> next{nullptr};
    std::atomic<uint32_t> enabled{0};
    std::atomic<uint32_t> retired{0};
    bool managed_callback_gate = false;
    std::unique_ptr<ExecutableMapping> stubs;
};

struct HookChain {
    void *target = nullptr;
    void *gateway = nullptr;
    void *root_original = nullptr;
    std::atomic<void *> head{nullptr};
    bool generation_target_authenticated = false;
    std::unique_ptr<ExecutableMapping> gateway_mapping;
    std::vector<std::unique_ptr<HookLayer>> layers;
};

static_assert(std::atomic<void *>::is_always_lock_free);
static_assert(sizeof(std::atomic<void *>) == sizeof(void *));
static_assert(alignof(std::atomic<void *>) >= alignof(void *));
static_assert(std::atomic<uint32_t>::is_always_lock_free);
static_assert(sizeof(std::atomic<uint32_t>) == sizeof(uint32_t));

std::mutex g_hook_broker_lock;
std::vector<std::unique_ptr<HookChain>> g_hook_chains;
thread_local std::string g_hook_broker_last_error;

const char *path_basename(const char *path) {
    if (path == nullptr)
        return nullptr;
    const char *separator = std::strrchr(path, '/');
    return separator == nullptr ? path : separator + 1;
}

bool address_matches_image(void *address, const char *soname) {
    if (address == nullptr || soname == nullptr)
        return false;
    Dl_info info{};
    return dladdr(address, &info) != 0 &&
           info.dli_fname != nullptr &&
           std::strcmp(path_basename(info.dli_fname), soname) == 0;
}

bool address_matches_symbol(
    void *address,
    const char *soname,
    const char *symbol) {
    if (address == nullptr || soname == nullptr || symbol == nullptr)
        return false;
    Dl_info info{};
    return dladdr(address, &info) != 0 &&
           info.dli_fname != nullptr &&
           info.dli_sname != nullptr &&
           std::strcmp(path_basename(info.dli_fname), soname) == 0 &&
           std::strcmp(info.dli_sname, symbol) == 0 &&
           info.dli_saddr == address;
}

bool trusted_host_hook_request(
    const char *owner,
    void *target,
    void *replacement,
    void *caller) {
    if (owner == nullptr || target == nullptr || replacement == nullptr ||
        caller == nullptr || !address_matches_image(target, "libil2cpp.so")) {
        return false;
    }

    if (address_matches_image(caller, "libAsyncInput.so") &&
        address_matches_image(replacement, "libAsyncInput.so") &&
        std::strncmp(owner, "ADOFAI.AsyncInput/", 18) == 0) {
        if (std::strcmp(owner, "ADOFAI.AsyncInput/il2cpp_init") == 0) {
            return address_matches_symbol(
                target, "libil2cpp.so", "il2cpp_init");
        }
        if (std::strcmp(owner, "ADOFAI.AsyncInput/il2cpp_init_utf16") == 0) {
            return address_matches_symbol(
                target, "libil2cpp.so", "il2cpp_init_utf16");
        }
        return true;
    }
    if (address_matches_image(caller, "libEditor_Pausemenu.so") &&
        address_matches_image(replacement, "libEditor_Pausemenu.so") &&
        std::strcmp(owner, "ADOFAI.EditorPause") == 0) {
        return true;
    }
    return address_matches_image(caller, "libadofai_extra_menu.so") &&
        address_matches_image(replacement, "libadofai_extra_menu.so");
}

HookChain *find_chain(void *target) {
    for (const auto &chain : g_hook_chains) {
        if (chain->target == target)
            return chain.get();
    }
    return nullptr;
}

HookLayer *find_reusable_layer(
    HookChain &chain,
    const std::string &owner,
    uint64_t generation,
    void *replacement,
    int compatibility_kind,
    bool managed_callback_gate) {
    for (const auto &layer : chain.layers) {
        if (layer->owner == owner &&
            layer->generation == generation &&
            layer->replacement == replacement &&
            layer->compatibility_kind == compatibility_kind &&
            layer->managed_callback_gate == managed_callback_gate &&
            layer->retired.load(std::memory_order_acquire) == 0) {
            return layer.get();
        }
    }
    return nullptr;
}

HookLayer *find_reactivatable_generation_layer(
    HookChain &chain,
    const std::string &owner,
    uint64_t generation,
    void *replacement,
    int compatibility_kind,
    bool managed_callback_gate) {
    if (generation == 0)
        return nullptr;
    for (const auto &layer : chain.layers) {
        if (layer->owner == owner &&
            layer->generation == generation &&
            layer->replacement == replacement &&
            layer->compatibility_kind == compatibility_kind &&
            layer->managed_callback_gate == managed_callback_gate &&
            layer->retired.load(std::memory_order_acquire) != 0) {
            return layer.get();
        }
    }
    return nullptr;
}

HookLayer *find_live_layer_identity(
    HookChain &chain,
    const std::string &owner,
    uint64_t generation,
    void *replacement,
    int compatibility_kind) {
    for (const auto &layer : chain.layers) {
        if (layer->owner == owner &&
            layer->generation == generation &&
            layer->replacement == replacement &&
            layer->compatibility_kind == compatibility_kind &&
            layer->retired.load(std::memory_order_acquire) == 0) {
            return layer.get();
        }
    }
    return nullptr;
}

bool replacement_is_live_in_other_chain(void *target, void *replacement) {
    for (const auto &chain : g_hook_chains) {
        if (chain->target == target)
            continue;
        for (const auto &layer : chain->layers) {
            if (layer->replacement == replacement &&
                layer->retired.load(std::memory_order_acquire) == 0) {
                return true;
            }
        }
    }
    return false;
}

constexpr size_t kDobbyNearBranchPatchSize = sizeof(uint32_t);

struct Aarch64EntryPatchPlan {
    size_t reservation_size =
        starray::native_patch::kConservativeDobbyArm64PatchSize;
    bool use_near_branch = false;
};

bool is_relocatable_short_leaf_instruction(uint32_t instruction) {
    // A one-instruction continuation is intentionally limited to register MOV
    // aliases and MOV-wide immediates. Neither form depends on the original PC.
    const bool move_register =
        (instruction & UINT32_C(0x7FE0FFE0)) == UINT32_C(0x2A0003E0);
    const bool move_wide_immediate =
        (instruction & UINT32_C(0x1F800000)) == UINT32_C(0x12800000);
    return move_register || move_wide_immediate;
}

bool validate_aarch64_patch_point(
    void *function,
    void *replacement,
    Aarch64EntryPatchPlan &patch_plan,
    std::string &error) {
    patch_plan = {};
#if defined(__aarch64__)
    if (function == nullptr || replacement == nullptr) {
        error = "patch point and replacement are required";
        return false;
    }

    const uintptr_t function_address = reinterpret_cast<uintptr_t>(function);
    const uintptr_t replacement_address = reinterpret_cast<uintptr_t>(replacement);
    const uint64_t distance = function_address >= replacement_address
        ? static_cast<uint64_t>(function_address - replacement_address)
        : static_cast<uint64_t>(replacement_address - function_address);
    const size_t patch_instruction_count = distance < (uint64_t{1} << 32u)
        ? 3u
        : 4u;

    uint32_t instructions[4]{};
    std::memcpy(instructions, function, sizeof(instructions));
    const uint32_t first = instructions[0];
    const uint32_t second = instructions[1];

    const auto is_direct_branch = [](uint32_t instruction) {
        return (instruction & 0xFC000000u) == 0x14000000u;
    };
    const auto is_register_branch = [](uint32_t instruction) {
        return (instruction & 0xFFFFFC1Fu) == 0xD61F0000u;
    };
    const auto is_return = [](uint32_t instruction) {
        return (instruction & 0xFFFFFC1Fu) == 0xD65F0000u;
    };
    const auto is_breakpoint = [](uint32_t instruction) {
        return (instruction & 0xFFE0001Fu) == 0xD4200000u;
    };
    const auto is_bti = [](uint32_t instruction) {
        return instruction == 0xD503241Fu ||
               instruction == 0xD503245Fu ||
               instruction == 0xD503249Fu ||
               instruction == 0xD50324DFu;
    };
    const auto is_ldr_literal_x = [](uint32_t instruction) {
        return (instruction & 0xFF000000u) == 0x58000000u;
    };

    const bool literal_register_branch =
        is_ldr_literal_x(first) &&
        is_register_branch(second) &&
        (first & 0x1Fu) == ((second >> 5u) & 0x1Fu);

    if (is_direct_branch(first) ||
        literal_register_branch ||
        (is_bti(first) && is_direct_branch(second))) {
        std::ostringstream message;
        message << "patch point already contains an unmanaged branch first=0x"
                << std::hex << first << " second=0x" << second;
        error = message.str();
        return false;
    }

    for (size_t index = 0; index + 1u < patch_instruction_count; ++index) {
        const uint32_t instruction = instructions[index];
        if (is_direct_branch(instruction) ||
            is_register_branch(instruction) ||
            is_return(instruction) ||
            is_breakpoint(instruction)) {
            if (index == 1u &&
                is_relocatable_short_leaf_instruction(first)) {
                patch_plan.reservation_size = kDobbyNearBranchPatchSize;
                patch_plan.use_near_branch = true;
                error.clear();
                return true;
            }
            std::ostringstream message;
            message << "patch point is shorter than Dobby's "
                    << std::dec << (patch_instruction_count * sizeof(uint32_t))
                    << "-byte trampoline; terminal instruction index="
                    << index << " value=0x" << std::hex << instruction;
            error = message.str();
            return false;
        }
    }
#else
    (void)function;
    (void)replacement;
#endif
    error.clear();
    return true;
}

#if defined(__aarch64__)

constexpr size_t kBranchStubSize = 24;
constexpr size_t kLayerEntryOffset = 0;
constexpr size_t kLayerEntrySize = 64;
constexpr size_t kLayerContinuationOffset = kLayerEntrySize;
constexpr size_t kLayerStubPayloadSize = kLayerEntrySize + kBranchStubSize;

uint32_t encode_ldr_literal_x(uint32_t target_register, int32_t byte_offset) {
    const int32_t word_offset = byte_offset / 4;
    return 0x58000000u |
           ((static_cast<uint32_t>(word_offset) & 0x7FFFFu) << 5u) |
           (target_register & 0x1Fu);
}

uint32_t encode_cbz(uint32_t target_register, int32_t byte_offset, bool is_64_bit) {
    const int32_t word_offset = byte_offset / 4;
    return (is_64_bit ? 0xB4000000u : 0x34000000u) |
           ((static_cast<uint32_t>(word_offset) & 0x7FFFFu) << 5u) |
           (target_register & 0x1Fu);
}

void write_u32(uint8_t *destination, size_t offset, uint32_t value) {
    std::memcpy(destination + offset, &value, sizeof(value));
}

void write_pointer(uint8_t *destination, size_t offset, const void *value) {
    std::memcpy(destination + offset, &value, sizeof(value));
}

bool build_branch_stub(
    std::atomic<void *> *destination,
    std::unique_ptr<ExecutableMapping> &mapping,
    void *&entry,
    std::string &error) {
    mapping = std::make_unique<ExecutableMapping>();
    if (!mapping->allocate(kBranchStubSize, error))
        return false;

    auto *code = static_cast<uint8_t *>(mapping->address());
    // x17 = &destination; spin until x16 = *destination; tail-branch to x16.
    write_u32(code, 0, encode_ldr_literal_x(17, 16));
    write_u32(code, 4, 0xC8DFFE30u); // ldar x16, [x17]
    write_u32(code, 8, encode_cbz(16, -4, true));
    write_u32(code, 12, 0xD61F0200u); // br x16
    write_pointer(code, 16, destination);
    if (!mapping->seal(kBranchStubSize, error))
        return false;
    entry = mapping->address();
    return true;
}

bool build_standard_layer_stubs(HookLayer &layer, std::string &error) {
    layer.stubs = std::make_unique<ExecutableMapping>();
    if (!layer.stubs->allocate(kLayerStubPayloadSize, error))
        return false;

    auto *code = static_cast<uint8_t *>(layer.stubs->address());
    // Entry preserves the target ABI. It either tail-branches to the MOD detour or
    // skips directly to the next broker node. Literals point to stable heap fields.
    write_u32(code, 0, encode_ldr_literal_x(17, 40));
    write_u32(code, 4, 0xB9400230u); // ldr w16, [x17]
    write_u32(code, 8, encode_cbz(16, 12, false));
    write_u32(code, 12, encode_ldr_literal_x(16, 36));
    write_u32(code, 16, 0xD61F0200u); // br x16
    write_u32(code, 20, encode_ldr_literal_x(17, 36));
    write_u32(code, 24, 0xC8DFFE30u); // ldar x16, [x17]
    write_u32(code, 28, encode_cbz(16, -4, true));
    write_u32(code, 32, 0xD61F0200u); // br x16
    write_u32(code, 36, 0xD503201Fu); // nop / alignment
    write_pointer(code, 40, &layer.enabled);
    write_pointer(code, 48, layer.replacement);
    write_pointer(code, 56, &layer.next);

    auto *continuation = code + kLayerContinuationOffset;
    write_u32(continuation, 0, encode_ldr_literal_x(17, 16));
    write_u32(continuation, 4, 0xC8DFFE30u); // ldar x16, [x17]
    write_u32(continuation, 8, encode_cbz(16, -4, true));
    write_u32(continuation, 12, 0xD61F0200u); // br x16
    write_pointer(continuation, 16, &layer.next);

    if (!layer.stubs->seal(kLayerStubPayloadSize, error))
        return false;
    layer.entry = code + kLayerEntryOffset;
    layer.continuation = continuation;
    return true;
}

bool build_calculate_tick_color_compatibility_stubs(
    HookLayer &layer,
    std::string &error) {
    // Actual ABI: x0 instance, s0/s1 floats, x1 hitFloor, x2 methodInfo.
    // Legacy ABI: x0 instance, s0/s1 floats, x1 methodInfo.
    // x19 carries hitFloor across the managed detour. Save and restore x19/lr
    // around the call so the adapter preserves the AAPCS64 caller contract.
    constexpr size_t kEntrySize = 88;
    constexpr size_t kContinuationSize = 32;
    constexpr size_t kPayloadSize = kEntrySize + kContinuationSize;
    layer.stubs = std::make_unique<ExecutableMapping>();
    if (!layer.stubs->allocate(kPayloadSize, error))
        return false;

    auto *code = static_cast<uint8_t *>(layer.stubs->address());
    write_u32(code, 0, encode_ldr_literal_x(17, 64));
    write_u32(code, 4, 0xB9400230u); // ldr w16, [x17]
    write_u32(code, 8, encode_cbz(16, 32, false));
    write_u32(code, 12, 0xA9BF7BF3u); // stp x19, x30, [sp, #-16]!
    write_u32(code, 16, 0xAA0103F3u); // mov x19, x1 (hitFloor)
    write_u32(code, 20, 0xAA0203E1u); // mov x1, x2 (methodInfo)
    write_u32(code, 24, encode_ldr_literal_x(16, 48));
    write_u32(code, 28, 0xD63F0200u); // blr x16
    write_u32(code, 32, 0xA8C17BF3u); // ldp x19, x30, [sp], #16
    write_u32(code, 36, 0xD65F03C0u); // ret
    write_u32(code, 40, encode_ldr_literal_x(17, 40));
    write_u32(code, 44, 0xC8DFFE30u); // ldar x16, [x17]
    write_u32(code, 48, encode_cbz(16, -4, true));
    write_u32(code, 52, 0xD61F0200u); // br x16
    write_u32(code, 56, 0xD503201Fu); // alignment nop
    write_pointer(code, 64, &layer.enabled);
    write_pointer(code, 72, layer.replacement);
    write_pointer(code, 80, &layer.next);

    auto *continuation = code + kEntrySize;
    write_u32(continuation, 0, 0xAA0103E2u); // mov x2, x1 (methodInfo)
    write_u32(continuation, 4, 0xAA1303E1u); // mov x1, x19 (hitFloor)
    write_u32(continuation, 8, encode_ldr_literal_x(17, 16));
    write_u32(continuation, 12, 0xC8DFFE30u); // ldar x16, [x17]
    write_u32(continuation, 16, encode_cbz(16, -4, true));
    write_u32(continuation, 20, 0xD61F0200u); // br x16
    write_pointer(continuation, 24, &layer.next);

    if (!layer.stubs->seal(kPayloadSize, error))
        return false;
    layer.entry = code;
    layer.continuation = continuation;
    return true;
}

#endif

#if !defined(__aarch64__)
bool build_nonarch_layer_stubs(HookLayer &, std::string &error) {
    error = "owner-gated HookBroker currently requires AArch64";
    return false;
}
#endif

bool build_layer_stubs(HookLayer &layer, std::string &error) {
#if defined(__aarch64__)
    if (layer.compatibility_kind ==
        MODMANAGER_HOOK_ABI_CALCULATE_TICK_COLOR_WITHOUT_HIT_FLOOR) {
        return build_calculate_tick_color_compatibility_stubs(layer, error);
    }
    return build_standard_layer_stubs(layer, error);
#else
    return build_nonarch_layer_stubs(layer, error);
#endif
}

void set_error(std::string message) {
    g_hook_broker_last_error = std::move(message);
    LOGE("%s", g_hook_broker_last_error.c_str());
}

bool valid_owner(const char *owner) {
    return owner != nullptr && owner[0] != '\0';
}

bool protect_generation_hook_pointer(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    uint32_t purpose,
    void *candidate,
    bool require_il2cpp_origin,
    void **protected_out,
    int *resolved_status_out = nullptr,
    uint32_t *descriptor_slot_out = nullptr);

bool layer_matches_owner_generation(
    const HookLayer &layer,
    const std::string &owner,
    uint64_t generation,
    bool exact_generation) {
    return layer.owner == owner &&
        (!exact_generation || layer.generation == generation);
}

int set_owner_enabled_locked(
    const std::string &owner,
    uint64_t generation,
    bool exact_generation,
    bool enabled) {
    int changed = 0;
    for (const auto &chain : g_hook_chains) {
        for (const auto &layer : chain->layers) {
            if (!layer_matches_owner_generation(
                    *layer, owner, generation, exact_generation) ||
                layer->retired.load(std::memory_order_acquire) != 0) {
                continue;
            }
            const uint32_t value = enabled ? 1u : 0u;
            if (layer->enabled.exchange(value, std::memory_order_acq_rel) != value)
                ++changed;
        }
    }
    return changed;
}

bool authenticate_generation_layers_for_enable_locked(
    const std::string &owner,
    uint64_t generation) {
    for (const auto &chain : g_hook_chains) {
        for (const auto &layer : chain->layers) {
            if (!layer_matches_owner_generation(
                    *layer, owner, generation, true) ||
                layer->retired.load(std::memory_order_acquire) != 0 ||
                layer->enabled.load(std::memory_order_acquire) != 0) {
                continue;
            }
            void *protected_replacement = nullptr;
            void *protected_continuation = nullptr;
            if (!protect_generation_hook_pointer(
                    owner.c_str(),
                    generation,
                    chain->target,
                    layer->replacement,
                    layer->compatibility_kind,
                    2,
                    layer->replacement,
                    false,
                    &protected_replacement) ||
                protected_replacement != layer->replacement ||
                !protect_generation_hook_pointer(
                    owner.c_str(),
                    generation,
                    chain->target,
                    layer->replacement,
                    layer->compatibility_kind,
                    3,
                    layer->continuation,
                    false,
                    &protected_continuation) ||
                protected_continuation != layer->continuation) {
                return false;
            }
        }
    }
    return true;
}

int retire_owner_locked(
    const std::string &owner,
    uint64_t generation,
    bool exact_generation,
    void *target) {
    int retired = 0;
    for (const auto &chain : g_hook_chains) {
        if (target != nullptr && chain->target != target)
            continue;
        for (const auto &layer : chain->layers) {
            if (!layer_matches_owner_generation(
                    *layer, owner, generation, exact_generation) ||
                layer->retired.load(std::memory_order_acquire) != 0) {
                continue;
            }
            layer->enabled.store(0, std::memory_order_release);
            layer->retired.store(1, std::memory_order_release);
            ++retired;
        }
    }
    return retired;
}

int count_owner_layers_locked(
    const std::string &owner,
    uint64_t generation,
    bool exact_generation,
    bool enabled_only) {
    int count = 0;
    for (const auto &chain : g_hook_chains) {
        for (const auto &layer : chain->layers) {
            if (!layer_matches_owner_generation(
                    *layer, owner, generation, exact_generation) ||
                layer->retired.load(std::memory_order_acquire) != 0) {
                continue;
            }
            if (!enabled_only || layer->enabled.load(std::memory_order_acquire) != 0)
                ++count;
        }
    }
    return count;
}

int count_owner_untracked_callback_layers_locked(
    const std::string &owner,
    uint64_t generation) {
    int count = 0;
    for (const auto &chain : g_hook_chains) {
        for (const auto &layer : chain->layers) {
            if (!layer_matches_owner_generation(
                    *layer, owner, generation, true) ||
                layer->retired.load(std::memory_order_acquire) != 0 ||
                layer->managed_callback_gate) {
                continue;
            }
            ++count;
        }
    }
    return count;
}

uint32_t generation_hook_descriptor_slot(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    uint32_t purpose) {
    uint32_t hash = UINT32_C(2166136261);
    for (const auto *cursor = reinterpret_cast<const uint8_t *>(owner);
         cursor != nullptr && *cursor != 0;
         ++cursor) {
        hash ^= *cursor;
        hash *= UINT32_C(16777619);
    }
    const uintptr_t values[] = {
        static_cast<uintptr_t>(generation),
        reinterpret_cast<uintptr_t>(target),
        reinterpret_cast<uintptr_t>(replacement),
        static_cast<uintptr_t>(static_cast<uint32_t>(compatibility_kind)),
        static_cast<uintptr_t>(purpose),
    };
    for (uintptr_t value : values) {
        for (size_t index = 0; index < sizeof(value); ++index) {
            hash ^= static_cast<uint8_t>(value >> (index * 8U));
            hash *= UINT32_C(16777619);
        }
    }
    return UINT32_C(0x74000000) | (hash & UINT32_C(0x00FFFFFF));
}

bool protect_generation_hook_pointer(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    uint32_t purpose,
    void *candidate,
    bool require_il2cpp_origin,
    void **protected_out,
    int *resolved_status_out,
    uint32_t *descriptor_slot_out) {
    if (candidate == nullptr || protected_out == nullptr)
        return false;
    uintptr_t protected_address = 0;
    const uint32_t slot = generation_hook_descriptor_slot(
        owner,
        generation,
        target,
        replacement,
        compatibility_kind,
        purpose);
    if (descriptor_slot_out != nullptr)
        *descriptor_slot_out = slot;
    const int resolved = require_il2cpp_origin
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
    if (resolved_status_out != nullptr)
        *resolved_status_out = resolved;
    if (resolved != 1 || protected_address != reinterpret_cast<uintptr_t>(candidate))
        return false;
    *protected_out = reinterpret_cast<void *>(protected_address);
    return true;
}

struct HookContinuationProtection {
    bool generation_bound = false;
    uint32_t module_id = 0;
    uint32_t operation_id = 0;
    uint32_t descriptor_slot = 0;
};

bool protect_hook_continuation(
    const HookContinuationProtection *protection,
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    void *candidate,
    void **protected_out) {
    if (candidate == nullptr || protected_out == nullptr)
        return false;
    if (protection == nullptr) {
        *protected_out = candidate;
        return true;
    }
    if (protection->generation_bound) {
        return protect_generation_hook_pointer(
            owner,
            generation,
            target,
            replacement,
            compatibility_kind,
            3,
            candidate,
            false,
            protected_out);
    }
    if (protection->module_id == 0 || protection->operation_id == 0 ||
        protection->descriptor_slot == 0) {
        return false;
    }
    uintptr_t protected_address = 0;
    if (PC_COMPAT_RESOLVE_CONTINUATION(
            protection->module_id,
            protection->operation_id,
            protection->descriptor_slot,
            reinterpret_cast<uintptr_t>(candidate),
            &protected_address) != 1 ||
        protected_address != reinterpret_cast<uintptr_t>(candidate)) {
        return false;
    }
    *protected_out = reinterpret_cast<void *>(protected_address);
    return true;
}

int try_publish_reusable_generation_hook(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    bool managed_callback_gate,
    void **continuation_out) {
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    HookChain *chain = find_chain(target);
    if (chain == nullptr)
        return 0;
    HookLayer *existing = find_reusable_layer(
        *chain,
        owner,
        generation,
        replacement,
        compatibility_kind,
        managed_callback_gate);
    bool reactivating = false;
    if (existing == nullptr) {
        existing = find_reactivatable_generation_layer(
            *chain,
            owner,
            generation,
            replacement,
            compatibility_kind,
            managed_callback_gate);
        reactivating = existing != nullptr;
    }
    if (existing == nullptr)
        return 0;

    void *protected_replacement = nullptr;
    void *protected_continuation = nullptr;
    if (!protect_generation_hook_pointer(
            owner, generation, target, replacement, compatibility_kind,
            2, replacement, false, &protected_replacement) ||
        protected_replacement != replacement ||
        !protect_generation_hook_pointer(
            owner, generation, target, replacement, compatibility_kind,
            3, existing->continuation, false, &protected_continuation)) {
        return -1;
    }
    if (reactivating) {
        existing->retired.store(0, std::memory_order_release);
        LOGI("reactivate owner=%s generation=%llu target=%p replacement=%p continuation=%p layers=%zu",
             owner,
             static_cast<unsigned long long>(generation),
             target,
             replacement,
             existing->continuation,
             chain->layers.size());
    }
    existing->enabled.store(1, std::memory_order_release);
    *continuation_out = protected_continuation;
    return 1;
}

bool generation_target_already_authenticated(void *target) {
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const HookChain *chain = find_chain(target);
    return chain != nullptr && chain->generation_target_authenticated;
}

void mark_generation_target_authenticated(void *target) {
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    HookChain *chain = find_chain(target);
    if (chain != nullptr)
        chain->generation_target_authenticated = true;
}

int install_hook_layer(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    bool managed_callback_gate,
    void **continuation_out,
    bool authenticate_target_page,
    const HookContinuationProtection *continuation_protection);

int install_protected_generation_hook_layer(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    uint32_t layer_flags,
    void **continuation_out) {
    if (!valid_owner(owner) || generation == 0 || target == nullptr ||
        replacement == nullptr || continuation_out == nullptr) {
        return -10;
    }
    *continuation_out = nullptr;
    if ((layer_flags & ~MODMANAGER_HOOK_LAYER_FLAG_MANAGED_CALLBACK_GATE) != 0)
        return -11;
    const bool managed_callback_gate =
        (layer_flags & MODMANAGER_HOOK_LAYER_FLAG_MANAGED_CALLBACK_GATE) != 0;

    const int reused = try_publish_reusable_generation_hook(
        owner,
        generation,
        target,
        replacement,
        compatibility_kind,
        managed_callback_gate,
        continuation_out);
    if (reused > 0)
        return 0;
    if (reused < 0) {
        set_error(std::string("generation hook reusable descriptor rejected owner=") + owner);
        return -12;
    }

    void *protected_target = nullptr;
    void *protected_replacement = nullptr;
    const bool target_is_il2cpp = address_matches_image(target, "libil2cpp.so");
    const bool target_authenticated = generation_target_already_authenticated(target);
    if (!target_authenticated) {
        int resolved_status = 0;
        uint32_t descriptor_slot = 0;
        if (!protect_generation_hook_pointer(
                owner, generation, target, replacement, compatibility_kind,
                1, target, target_is_il2cpp, &protected_target,
                &resolved_status, &descriptor_slot)) {
            std::ostringstream message;
            message << "generation hook target descriptor rejected owner=" << owner
                    << " target=" << target
                    << " replacement=" << replacement
                    << " slot=0x" << std::hex << descriptor_slot
                    << " resolver=" << std::dec << resolved_status
                    << " il2cpp=" << (target_is_il2cpp ? 1 : 0);
            set_error(message.str());
            return -12;
        }
    }
    int replacement_resolved_status = 0;
    uint32_t replacement_descriptor_slot = 0;
    if (!protect_generation_hook_pointer(
            owner, generation, target, replacement, compatibility_kind,
            2, replacement, false, &protected_replacement,
            &replacement_resolved_status, &replacement_descriptor_slot)) {
        std::ostringstream message;
        message << "generation hook replacement descriptor rejected owner=" << owner
                << " target=" << target
                << " replacement=" << replacement
                << " slot=0x" << std::hex << replacement_descriptor_slot
                << " resolver=" << std::dec << replacement_resolved_status;
        set_error(message.str());
        return -12;
    }
    if (target_authenticated)
        protected_target = target;

    void *continuation = nullptr;
    const HookContinuationProtection continuation_protection{
        .generation_bound = true,
    };
    const int result = install_hook_layer(
        owner,
        generation,
        protected_target,
        protected_replacement,
        compatibility_kind,
        managed_callback_gate,
        &continuation,
        true,
        &continuation_protection);
    if (result != 0)
        return result;
    mark_generation_target_authenticated(protected_target);
    *continuation_out = continuation;
    return 0;
}

int install_hook_layer(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    bool managed_callback_gate,
    void **continuation_out,
    bool authenticate_target_page,
    const HookContinuationProtection *continuation_protection) {
    if (target == nullptr || replacement == nullptr || continuation_out == nullptr) {
        set_error("install rejected: target, replacement and continuation_out are required");
        return -1;
    }
    if (target == replacement) {
        set_error("install rejected: target equals replacement");
        return -2;
    }
    if (compatibility_kind != MODMANAGER_HOOK_ABI_NONE &&
        compatibility_kind != MODMANAGER_HOOK_ABI_CALCULATE_TICK_COLOR_WITHOUT_HIT_FLOOR) {
        set_error("install rejected: unknown ABI compatibility kind");
        return -8;
    }

    const std::string owner_name = valid_owner(owner) ? owner : "anonymous";
    starray::native_patch::Transaction patch_transaction;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    auto *chain = find_chain(target);
    if (chain != nullptr) {
        if (auto *identity = find_live_layer_identity(
                *chain,
                owner_name,
                generation,
                replacement,
                compatibility_kind);
            identity != nullptr &&
            identity->managed_callback_gate != managed_callback_gate) {
            set_error(
                "install rejected: callback gate classification changed for live layer owner=" +
                owner_name);
            return -14;
        }
        if (auto *existing = find_reusable_layer(
                *chain,
                owner_name,
                generation,
                replacement,
                compatibility_kind,
                managed_callback_gate);
            existing != nullptr) {
            void *protected_continuation = existing->continuation;
            if (!protect_hook_continuation(
                    continuation_protection,
                    owner_name.c_str(),
                    generation,
                    target,
                    replacement,
                    compatibility_kind,
                    existing->continuation,
                    &protected_continuation) ||
                protected_continuation != existing->continuation) {
                set_error(
                    "protected hook continuation descriptor rejected before reuse owner=" +
                    owner_name);
                return -13;
            }
            existing->enabled.store(1, std::memory_order_release);
            *continuation_out = protected_continuation;
            g_hook_broker_last_error.clear();
            LOGI("reuse owner=%s generation=%llu target=%p replacement=%p continuation=%p layers=%zu",
                  owner_name.c_str(),
                  static_cast<unsigned long long>(generation),
                  target,
                 replacement,
                 existing->continuation,
                 chain->layers.size());
            return 0;
        }
    }

    if (replacement_is_live_in_other_chain(target, replacement)) {
        set_error("install rejected: replacement is already bound to another target");
        return -3;
    }

#if !defined(__aarch64__)
    set_error("owner-gated HookBroker currently requires AArch64");
    return -6;
#else
    auto prepared_layer = std::make_unique<HookLayer>();
    prepared_layer->owner = owner_name;
    prepared_layer->generation = generation;
    prepared_layer->replacement = replacement;
    prepared_layer->compatibility_kind = compatibility_kind;
    prepared_layer->managed_callback_gate = managed_callback_gate;
    std::string stub_error;
    if (!build_layer_stubs(*prepared_layer, stub_error)) {
        set_error("install rejected for owner=" + owner_name + ": " + stub_error);
        return -7;
    }
    void *protected_continuation = prepared_layer->continuation;
    if (!protect_hook_continuation(
            continuation_protection,
            owner_name.c_str(),
            generation,
            target,
            replacement,
            compatibility_kind,
            prepared_layer->continuation,
            &protected_continuation) ||
        protected_continuation != prepared_layer->continuation) {
        set_error(
            "protected hook continuation descriptor rejected before publication owner=" +
            owner_name);
        return -13;
    }

    if (chain == nullptr) {
        auto prepared_chain = std::make_unique<HookChain>();
        prepared_chain->target = target;
        prepared_chain->layers.reserve(1);
        g_hook_chains.reserve(g_hook_chains.size() + 1);
        if (!build_branch_stub(
                &prepared_chain->head,
                prepared_chain->gateway_mapping,
                prepared_chain->gateway,
                stub_error)) {
            set_error("install rejected for owner=" + owner_name + ": " + stub_error);
            return -7;
        }

        std::string entry_error;
        Aarch64EntryPatchPlan patch_plan;
        if (!validate_aarch64_patch_point(
                target, prepared_chain->gateway, patch_plan, entry_error)) {
            set_error("install rejected for owner=" + owner_name + ": " + entry_error);
            return -4;
        }
        starray::native_patch::ReservationToken patch_reservation;
        std::string reservation_error;
        const auto reservation_result = patch_transaction.Reserve(
            starray::native_patch::Kind::Hook,
            "hook-broker",
            owner_name.c_str(),
            generation,
            target,
            patch_plan.reservation_size,
            patch_reservation,
            reservation_error);
        if (reservation_result != starray::native_patch::ReserveResult::Acquired) {
            set_error(
                "install rejected for owner=" + owner_name + ": " +
                (reservation_error.empty()
                    ? "hook target already has an unowned physical reservation"
                    : reservation_error));
            return -10;
        }
        if (!starray::code_patch::PrepareHookBrokerWrite(
                target,
                patch_plan.reservation_size,
                authenticate_target_page)) {
            set_error(
                "install rejected for owner=" + owner_name +
                ": code patch page changed before HookBroker write");
            return -11;
        }

        prepared_layer->enabled.store(1, std::memory_order_relaxed);
        prepared_chain->head.store(prepared_layer->entry, std::memory_order_release);
        void *root_original = nullptr;
        uint8_t entry_before[starray::native_patch::kConservativeDobbyArm64PatchSize]{};
        if (patch_plan.use_near_branch) {
            std::memcpy(entry_before, target, sizeof(entry_before));
            dobby_enable_near_branch_trampoline();
        }
        const int rc = DobbyHook(
            target,
            reinterpret_cast<dobby_dummy_func_t>(prepared_chain->gateway),
            reinterpret_cast<dobby_dummy_func_t *>(&root_original));
        if (patch_plan.use_near_branch)
            dobby_disable_near_branch_trampoline();
        if (rc != 0 || root_original == nullptr) {
            if (rc == 0)
                patch_transaction.Commit(patch_reservation);
            std::ostringstream message;
            message << "DobbyHook failed owner=" << owner_name
                    << " target=" << target
                    << " gateway=" << prepared_chain->gateway
                    << " replacement=" << replacement
                    << " rc=" << rc
                    << " continuation=" << root_original;
            set_error(message.str());
            return rc != 0 ? rc : -5;
        }
        if (patch_plan.use_near_branch) {
            uint32_t patched_entry = 0;
            std::memcpy(&patched_entry, target, sizeof(patched_entry));
            const bool direct_near_branch =
                (patched_entry & UINT32_C(0xFC000000)) == UINT32_C(0x14000000);
            const bool tail_unchanged = std::memcmp(
                static_cast<const uint8_t *>(target) + kDobbyNearBranchPatchSize,
                entry_before + kDobbyNearBranchPatchSize,
                sizeof(entry_before) - kDobbyNearBranchPatchSize) == 0;
            if (!direct_near_branch || !tail_unchanged) {
                const int destroy_rc = DobbyDestroy(target);
                std::ostringstream message;
                message << "Dobby near-branch verification failed owner="
                        << owner_name
                        << " target=" << target
                        << " first=0x" << std::hex << patched_entry
                        << " tailUnchanged=" << std::dec << (tail_unchanged ? 1 : 0)
                        << " destroyRc=" << destroy_rc;
                set_error(message.str());
                return -15;
            }
        }
        patch_transaction.Commit(patch_reservation);
        starray::code_patch::CommitHookBrokerWrite(
            target,
            patch_plan.reservation_size);

        prepared_chain->root_original = root_original;
        prepared_layer->next.store(root_original, std::memory_order_release);
        *continuation_out = protected_continuation;
        prepared_chain->layers.push_back(std::move(prepared_layer));
        g_hook_chains.push_back(std::move(prepared_chain));
        chain = g_hook_chains.back().get();
    } else {
        chain->layers.reserve(chain->layers.size() + 1);
        prepared_layer->next.store(
            chain->head.load(std::memory_order_acquire),
            std::memory_order_relaxed);
        prepared_layer->enabled.store(1, std::memory_order_relaxed);
        *continuation_out = protected_continuation;
        auto *published_layer = prepared_layer.get();
        chain->layers.push_back(std::move(prepared_layer));
        chain->head.store(published_layer->entry, std::memory_order_release);
    }

    g_hook_broker_last_error.clear();
    LOGI("installed owner=%s generation=%llu target=%p gateway=%p replacement=%p continuation=%p layers=%zu",
         owner_name.c_str(),
         static_cast<unsigned long long>(generation),
         target,
         chain->gateway,
         replacement,
         *continuation_out,
         chain->layers.size());
    return 0;
#endif
}

} // namespace

extern "C" {

int modmanager_hook_broker_install(
    const char *owner,
    void *target,
    void *replacement,
    void **continuation_out) {
    void *caller = __builtin_extract_return_addr(__builtin_return_address(0));
    const bool authenticate_target_page =
        trusted_host_hook_request(owner, target, replacement, caller);
    if (address_matches_image(target, "libil2cpp.so") &&
        !authenticate_target_page) {
        if (continuation_out != nullptr)
            *continuation_out = nullptr;
        set_error("untrusted non-generation IL2CPP hook rejected");
        return -12;
    }
    return install_hook_layer(
        owner,
        0,
        target,
        replacement,
        MODMANAGER_HOOK_ABI_NONE,
        false,
        continuation_out,
        authenticate_target_page,
        nullptr);
}

int modmanager_hook_broker_install_compatible(
    const char *owner,
    void *target,
    void *replacement,
    int compatibility_kind,
    void **continuation_out) {
    if (address_matches_image(target, "libil2cpp.so")) {
        if (continuation_out != nullptr)
            *continuation_out = nullptr;
        set_error(
            "untrusted non-generation compatible IL2CPP hook rejected");
        return -12;
    }
    return install_hook_layer(
        owner,
        0,
        target,
        replacement,
        compatibility_kind,
        false,
        continuation_out,
        false,
        nullptr);
}

STARRAY_NATIVE_INTERNAL int modmanager_hook_broker_install_protected(
    const char *owner,
    uint32_t module_id,
    uint32_t operation_id,
    uint32_t descriptor_slot,
    void *target,
    void *replacement,
    void **continuation_out) {
    if (!valid_owner(owner) || module_id == 0 || operation_id == 0 ||
        descriptor_slot == 0 || target == nullptr || replacement == nullptr ||
        continuation_out == nullptr) {
        return -10;
    }
    *continuation_out = nullptr;

    uintptr_t protected_target = 0;
    uintptr_t protected_replacement = 0;
    if (PC_COMPAT_RESOLVE_ADDRESS(
            module_id,
            operation_id,
            descriptor_slot ^ UINT32_C(0x00800000),
            0 |
                0,
            reinterpret_cast<uintptr_t>(target),
            &protected_target) != 1 ||
        protected_target != reinterpret_cast<uintptr_t>(target) ||
        PC_COMPAT_RESOLVE_CONTINUATION(
            module_id,
            operation_id,
            descriptor_slot ^ UINT32_C(0x00400000),
            reinterpret_cast<uintptr_t>(replacement),
            &protected_replacement) != 1 ||
        protected_replacement != reinterpret_cast<uintptr_t>(replacement)) {
        set_error(std::string("protected host hook target/replacement descriptor rejected owner=") + owner);
        return -12;
    }

    const HookContinuationProtection continuation_protection{
        .module_id = module_id,
        .operation_id = operation_id,
        .descriptor_slot = descriptor_slot,
    };
    return install_hook_layer(
        owner,
        0,
        reinterpret_cast<void *>(protected_target),
        reinterpret_cast<void *>(protected_replacement),
        MODMANAGER_HOOK_ABI_NONE,
        false,
        continuation_out,
        true,
        &continuation_protection);
}

int modmanager_hook_broker_install_generation(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    void **continuation_out) {
    return install_protected_generation_hook_layer(
        owner,
        generation,
        target,
        replacement,
        MODMANAGER_HOOK_ABI_NONE,
        0,
        continuation_out);
}

int modmanager_hook_broker_install_generation_v2(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    uint32_t layer_flags,
    void **continuation_out) {
    return install_protected_generation_hook_layer(
        owner,
        generation,
        target,
        replacement,
        MODMANAGER_HOOK_ABI_NONE,
        layer_flags,
        continuation_out);
}

int modmanager_hook_broker_install_compatible_generation(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    void **continuation_out) {
    return install_protected_generation_hook_layer(
        owner,
        generation,
        target,
        replacement,
        compatibility_kind,
        0,
        continuation_out);
}

int modmanager_hook_broker_install_compatible_generation_v2(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    uint32_t layer_flags,
    void **continuation_out) {
    return install_protected_generation_hook_layer(
        owner,
        generation,
        target,
        replacement,
        compatibility_kind,
        layer_flags,
        continuation_out);
}

int modmanager_hook_broker_supports_owner_control(void) {
#if defined(__aarch64__)
    return 1;
#else
    return 0;
#endif
}

int modmanager_hook_broker_set_owner_enabled(const char *owner, int enabled) {
    if (!valid_owner(owner))
        return -1;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const int changed = set_owner_enabled_locked(owner, 0, false, enabled != 0);
    LOGI("owner state owner=%s enabled=%d changed=%d", owner, enabled != 0 ? 1 : 0, changed);
    return changed;
}

int modmanager_hook_broker_set_owner_generation_enabled(
    const char *owner,
    uint64_t generation,
    int enabled) {
    if (!valid_owner(owner) || generation == 0)
        return -1;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    if (enabled != 0 &&
        !authenticate_generation_layers_for_enable_locked(owner, generation)) {
        set_error(
            std::string("owner generation enable descriptor rejected owner=") + owner);
        return -2;
    }
    const int changed = set_owner_enabled_locked(
        owner, generation, true, enabled != 0);
    LOGI("owner generation state owner=%s generation=%llu enabled=%d changed=%d",
         owner,
         static_cast<unsigned long long>(generation),
         enabled != 0 ? 1 : 0,
         changed);
    return changed;
}

int modmanager_hook_broker_retire_owner_target(const char *owner, void *target) {
    if (!valid_owner(owner) || target == nullptr)
        return -1;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const int retired = retire_owner_locked(owner, 0, false, target);
    LOGI("owner target retired owner=%s target=%p layers=%d", owner, target, retired);
    return retired;
}

int modmanager_hook_broker_retire_owner_generation_target(
    const char *owner,
    uint64_t generation,
    void *target) {
    if (!valid_owner(owner) || generation == 0 || target == nullptr)
        return -1;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const int retired = retire_owner_locked(owner, generation, true, target);
    LOGI("owner generation target retired owner=%s generation=%llu target=%p layers=%d",
         owner,
         static_cast<unsigned long long>(generation),
         target,
         retired);
    return retired;
}

int modmanager_hook_broker_retire_owner(const char *owner) {
    if (!valid_owner(owner))
        return -1;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const int retired = retire_owner_locked(owner, 0, false, nullptr);
    LOGI("owner retired owner=%s layers=%d", owner, retired);
    return retired;
}

int modmanager_hook_broker_retire_owner_generation(
    const char *owner,
    uint64_t generation) {
    if (!valid_owner(owner) || generation == 0)
        return -1;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const int retired = retire_owner_locked(owner, generation, true, nullptr);
    LOGI("owner generation retired owner=%s generation=%llu layers=%d",
         owner,
         static_cast<unsigned long long>(generation),
         retired);
    return retired;
}

int modmanager_hook_broker_get_owner_retained_layer_count(const char *owner) {
    if (!valid_owner(owner))
        return 0;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    return count_owner_layers_locked(owner, 0, false, false);
}

int modmanager_hook_broker_get_owner_generation_retained_layer_count(
    const char *owner,
    uint64_t generation) {
    if (!valid_owner(owner) || generation == 0)
        return 0;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    return count_owner_layers_locked(owner, generation, true, false);
}

int modmanager_hook_broker_get_owner_generation_untracked_callback_layer_count(
    const char *owner,
    uint64_t generation) {
    if (!valid_owner(owner) || generation == 0)
        return 0;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    return count_owner_untracked_callback_layers_locked(owner, generation);
}

int modmanager_hook_broker_get_owner_enabled_layer_count(const char *owner) {
    if (!valid_owner(owner))
        return 0;
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    return count_owner_layers_locked(owner, 0, false, true);
}

int modmanager_hook_broker_get_chain_count(void) {
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    return static_cast<int>(g_hook_chains.size());
}

int modmanager_hook_broker_get_layer_count(void *target) {
    std::lock_guard<std::mutex> guard(g_hook_broker_lock);
    const auto *chain = find_chain(target);
    return chain == nullptr ? 0 : static_cast<int>(chain->layers.size());
}

int modmanager_hook_broker_copy_pristine_page(
    const void *page_base,
    size_t page_size,
    void *output,
    size_t output_size) {
    return starray::code_patch::CopyAuthenticatedPristinePage(
        page_base, page_size, output, output_size);
}

int modmanager_hook_broker_apply_authenticated_code_patch(
    const char *owner,
    void *target,
    const void *expected,
    const void *replacement,
    size_t size) {
    constexpr size_t kMaximumAuthenticatedCodePatchSize = 16;
    void *caller = __builtin_extract_return_addr(__builtin_return_address(0));
    if (owner == nullptr ||
        std::strcmp(owner, "ADOFAI.ExtraMenu/angle-judgment") != 0 ||
        target == nullptr || expected == nullptr || replacement == nullptr ||
        size == 0 || size > kMaximumAuthenticatedCodePatchSize ||
        !address_matches_image(caller, "libadofai_extra_menu.so") ||
        !address_matches_image(target, "libil2cpp.so")) {
        LOGE("authenticated code patch caller rejected owner=%s target=%p size=%zu",
             owner != nullptr ? owner : "<null>", target, size);
        return -1;
    }

    uint8_t expected_bytes[kMaximumAuthenticatedCodePatchSize]{};
    uint8_t replacement_bytes[kMaximumAuthenticatedCodePatchSize]{};
    std::memcpy(expected_bytes, expected, size);
    std::memcpy(replacement_bytes, replacement, size);

    starray::native_patch::Transaction patch_transaction;
    starray::native_patch::ReservationToken patch_reservation;
    std::string reservation_error;
    const auto reservation_result = patch_transaction.Reserve(
        starray::native_patch::Kind::CodePatch,
        "authenticated-host-code-patch",
        owner,
        0,
        target,
        size,
        patch_reservation,
        reservation_error);
    if (reservation_result != starray::native_patch::ReserveResult::Acquired &&
        reservation_result != starray::native_patch::ReserveResult::Reused) {
        LOGE("authenticated code patch reservation rejected owner=%s target=%p size=%zu error=%s",
             owner, target, size, reservation_error.c_str());
        return -2;
    }
    if (std::memcmp(target, expected_bytes, size) != 0) {
        LOGE("authenticated code patch expected bytes changed owner=%s target=%p size=%zu",
             owner, target, size);
        return -3;
    }
    if (!starray::code_patch::PrepareHookBrokerWrite(target, size, true)) {
        LOGE("authenticated code patch page state rejected owner=%s target=%p size=%zu",
             owner, target, size);
        return -4;
    }

    const int result = DobbyCodePatch(target, replacement_bytes, size);
    if (result != 0) {
        LOGE("authenticated code patch failed owner=%s target=%p size=%zu rc=%d",
             owner, target, size, result);
        return result;
    }
    patch_transaction.Commit(patch_reservation);
    starray::code_patch::CommitHookBrokerWrite(target, size);
    LOGI("authenticated code patch installed owner=%s target=%p size=%zu",
         owner, target, size);
    return 0;
}

const AdoModManagerHookBrokerApiV1 *modmanager_hook_broker_get_api_v1(void) {
    static const AdoModManagerHookBrokerApiV1 api = {
        sizeof(AdoModManagerHookBrokerApiV1),
        ADO_MODMANAGER_HOOK_BROKER_ABI_VERSION,
        ADO_MODMANAGER_HOOK_BROKER_ABI_MAGIC,
        &modmanager_hook_broker_install,
        &modmanager_hook_broker_retire_owner_target,
        &modmanager_hook_broker_apply_authenticated_code_patch,
    };
    return &api;
}

const char *modmanager_hook_broker_get_last_error(void) {
    return g_hook_broker_last_error.c_str();
}

} // extern "C"

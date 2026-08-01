#include "il2cpp_foreign_thread_guard.h"

#include <android/log.h>
#include <dlfcn.h>
#include <pthread.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_set>
#include <vector>

#include <dobby.h>

#define LOG_TAG "StArray.PcCompat.ThreadGuard"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace starray::il2cpp_thread_guard {
namespace {

using DomainGetFn = void *(*)();
using ThreadCurrentFn = void *(*)();
using ThreadAttachFn = void *(*)(void *);

DomainGetFn p_domain_get = nullptr;
ThreadCurrentFn p_thread_current = nullptr;
ThreadAttachFn p_thread_attach = nullptr;

void *g_domain = nullptr;
std::atomic<bool> g_ready{false};
std::once_flag g_install_once;

// Set while an attach is in flight on the current thread. il2cpp_thread_attach
// itself reaches instrumented code paths (monitor/GC helpers), and the
// preamble must not start a second, recursive attach from them.
pthread_key_t g_in_attach_key = 0;
bool g_in_attach_key_ready = false;

std::atomic<uint32_t> g_headroom_skip_count{0};

// Attaching registers the thread with boehm GC (GC_register_my_thread ->
// pthread_getattr_np) and allocates the Il2CppThread bookkeeping, which costs
// several KB of stack. Threads with less than this remaining must not attach.
constexpr uintptr_t kMinAttachStackHeadroom = 64 * 1024;

// NOTE on thread exit: boehm registers its own pthread-key destructor when a
// thread is registered, and pthread runs destructors of earlier-created keys
// first. Boehm's key is created at libil2cpp init, long before this guard
// installs, so by the time any key destructor of ours could run, boehm has
// already unregistered the thread — and il2cpp_thread_detach then looks the
// current thread up in the GC table, misses, and dereferences null (observed:
// SIGSEGV at libil2cpp Thread::Detach -> GC table mark, fault addr 0x20).
// There is no exit-time point at which detach is safe, so attached threads
// are intentionally never detached: boehm self-cleans the GC registration,
// and the il2cpp-side Il2CppThread bookkeeping simply leaks (bounded by
// foreign-thread churn; pool threads are long-lived).

// False when the calling thread does not have enough stack left to survive
// il2cpp_thread_attach. Such threads (tiny Java library stacks, threads
// nearly exhausted by deep managed recursion) keep running unguarded —
// exactly the behavior they had before this guard existed — instead of
// overflowing inside attach.
bool has_attach_stack_headroom() {
    pthread_attr_t attr;
    if (pthread_getattr_np(pthread_self(), &attr) != 0)
        return false;
    void *stack_base = nullptr;
    size_t stack_size = 0;
    const int rc = pthread_attr_getstack(&attr, &stack_base, &stack_size);
    pthread_attr_destroy(&attr);
    if (rc != 0 || stack_base == nullptr)
        return false;
    const uintptr_t current = reinterpret_cast<uintptr_t>(&stack_size);
    const uintptr_t low = reinterpret_cast<uintptr_t>(stack_base);
    if (current <= low)
        return false;
    return (current - low) >= kMinAttachStackHeadroom;
}

// Dobby instrument pre-handler, runs at the entry of every guarded export.
// Deliberately uses no __thread state: the emutls prologue for a TLS read
// faulted on a small-stack Java thread (PlayBilling) that reached an
// instrumented export. il2cpp_thread_current is a cheap TLS read inside
// libil2cpp and already reports threads this guard attached, so re-checking
// on every entry is both correct and allocation/stack free.
void guard_preamble(void *address, DobbyRegisterContext *ctx) {
    (void)address;
    (void)ctx;
    if (!g_ready.load(std::memory_order_acquire))
        return;
    if (p_thread_current() != nullptr)
        return;
    // A re-entrant preamble while this thread is already attaching would
    // recurse into a second attach; let the inner call run unguarded.
    if (g_in_attach_key_ready && pthread_getspecific(g_in_attach_key) != nullptr)
        return;
    if (!has_attach_stack_headroom()) {
        if (g_headroom_skip_count.fetch_add(1, std::memory_order_relaxed) < 5)
            LOGW("skipping il2cpp attach: low stack headroom");
        return;
    }
    if (g_in_attach_key_ready)
        pthread_setspecific(g_in_attach_key, reinterpret_cast<void *>(1));
    void *handle = p_thread_attach(g_domain);
    if (g_in_attach_key_ready)
        pthread_setspecific(g_in_attach_key, nullptr);
    if (handle == nullptr)
        return;
    LOGI("attached foreign thread to il2cpp domain (handle=%p)", handle);
}

struct GuardTarget {
    const char *symbol;
    bool required;
};

// Exports that can allocate managed heap / otherwise require an attached
// thread. Metadata readers (class_from_name, method_get_*, ...) do not
// allocate and are deliberately excluded to keep instrumentation overhead
// off hot lookup paths.
constexpr GuardTarget kGuardTargets[] = {
    {"il2cpp_runtime_invoke", true},
    {"il2cpp_runtime_invoke_convert_args", true},
    {"il2cpp_object_new", true},
    {"il2cpp_runtime_class_init", true},
    {"il2cpp_runtime_object_init", false},
    {"il2cpp_runtime_object_init_exception", false},
    {"il2cpp_value_box", false},
    {"il2cpp_array_new", false},
    {"il2cpp_array_new_specific", false},
    {"il2cpp_array_new_full", false},
    {"il2cpp_string_new", false},
    {"il2cpp_string_new_len", false},
    {"il2cpp_string_new_utf16", false},
    {"il2cpp_string_new_wrapper", false},
    {"il2cpp_string_intern", false},
    {"il2cpp_gchandle_new", false},
    {"il2cpp_gchandle_new_weakref", false},
    {"il2cpp_field_get_value_object", false},
    {"il2cpp_method_get_object", false},
    {"il2cpp_type_get_object", false},
    {"il2cpp_custom_attrs_construct", false},
    {"il2cpp_exception_from_name_msg", false},
    {"il2cpp_get_exception_argument_null", false},
    {"il2cpp_monitor_enter", false},
    {"il2cpp_monitor_try_enter", false},
    {"il2cpp_monitor_wait", false},
    {"il2cpp_monitor_try_wait", false},
    {"il2cpp_gc_collect", false},
    {"il2cpp_gc_collect_a_little", false},
    {"il2cpp_unhandled_exception", false},
    {"il2cpp_format_exception", false},
    {"il2cpp_format_stack_trace", false},
};

constexpr uint32_t kArm64BranchMask = 0xFC000000u;
constexpr uint32_t kArm64BranchOpcode = 0x14000000u;
constexpr uint32_t kArm64BranchWithLinkOpcode = 0x94000000u;
constexpr uint32_t kArm64RetOpcode = 0xD65F03C0u;

// Radius of the install-time scan for direct branches into a patch window's
// interior. An offline audit of the full .text (all 28 resolved windows)
// found zero interior branches at any distance, so a +-4K local scan is
// sufficient; it runs once per export at install.
constexpr int kInteriorScanRadius = 4096;

uint32_t read_word(const void *address) {
    uint32_t word;
    std::memcpy(&word, address, sizeof(word));
    return word;
}

int32_t sign_extend(uint32_t value, int bits) {
    const uint32_t sign = 1u << (bits - 1);
    if ((value & sign) != 0)
        value |= ~((1u << bits) - 1u);
    return static_cast<int32_t>(value);
}

void *decode_branch_target(const void *address, uint32_t instruction) {
    const int32_t imm26 = sign_extend(instruction & 0x03FFFFFFu, 26);
    const intptr_t base = reinterpret_cast<intptr_t>(address);
    return reinterpret_cast<void *>(base + (static_cast<intptr_t>(imm26) << 2));
}

// Returns the target of a direct branch instruction (b/bl/b.cond/cbz/cbnz/
// tbz/tbnz) located at `at`, or -1 if the word is not a direct branch.
int64_t direct_branch_target(uintptr_t at, uint32_t w) {
    if ((w & kArm64BranchMask) == kArm64BranchOpcode ||
        (w & kArm64BranchMask) == kArm64BranchWithLinkOpcode) {
        return static_cast<int64_t>(at) +
               (static_cast<int64_t>(sign_extend(w & 0x03FFFFFFu, 26)) << 2);
    }
    if ((w & 0xFF000010u) == 0x54000000u) {  // b.cond
        return static_cast<int64_t>(at) +
               (static_cast<int64_t>(sign_extend((w >> 5) & 0x7FFFFu, 19)) << 2);
    }
    if ((w & 0x7E000000u) == 0x34000000u) {  // cbz/cbnz
        return static_cast<int64_t>(at) +
               (static_cast<int64_t>(sign_extend((w >> 5) & 0x7FFFFu, 19)) << 2);
    }
    if ((w & 0x7E000000u) == 0x36000000u) {  // tbz/tbnz
        return static_cast<int64_t>(at) +
               (static_cast<int64_t>(sign_extend((w >> 5) & 0x3FFFu, 14)) << 2);
    }
    return -1;
}

// Instructions that commit to a stack frame. A `b` sitting behind one of
// these belongs to a real function body, not to a jump-thunk chain.
bool is_frame_setup(uint32_t w) {
    if ((w & 0xFF00001Fu) == 0xD100001Fu)  // sub (imm), Rd=sp
        return true;
    if ((w & 0xFE000000u) == 0xA8000000u)  // stp variants
        return true;
    if ((w & 0xFF000000u) == 0xF8000000u)  // str pre-index family
        return true;
    return false;
}

// True if any nearby direct branch targets (candidate, candidate+16) — the
// interior of the 16-byte Dobby entry patch. Patching over such a site would
// let the incoming branch land inside the trampoline jump (this is exactly
// how il2cpp_object_new's exception-cleanup path re-enters its epilogue).
bool has_interior_branch(const void *candidate) {
    const uintptr_t base = reinterpret_cast<uintptr_t>(candidate);
    const uintptr_t begin = base - kInteriorScanRadius;
    const uintptr_t end = base + 16 + kInteriorScanRadius;
    for (uintptr_t at = begin; at < end; at += 4) {
        if (at >= base && at < base + 16)
            continue;  // short skips inside the window itself are fine
        const int64_t target =
            direct_branch_target(at, read_word(reinterpret_cast<const void *>(at)));
        if (target > static_cast<int64_t>(base) &&
            target < static_cast<int64_t>(base + 16)) {
            LOGW("ThreadGuard: patch window %#lx has interior branch %#lx -> %#llx",
                 (unsigned long)base, (unsigned long)at, (long long)target);
            return true;
        }
    }
    return false;
}

// Exactly one bl among the first 4 words -> its target, else nullptr.
void *single_bl_target(const void *candidate) {
    void *result = nullptr;
    int count = 0;
    for (int i = 0; i < 4; ++i) {
        const uintptr_t at = reinterpret_cast<uintptr_t>(candidate) + 4 * i;
        const uint32_t w = read_word(reinterpret_cast<const void *>(at));
        if ((w & kArm64BranchMask) == kArm64BranchWithLinkOpcode) {
            result = decode_branch_target(reinterpret_cast<const void *>(at), w);
            ++count;
        }
    }
    return count == 1 ? result : nullptr;
}

enum class PatchSite { kOk, kTooShort, kInteriorBranch };

PatchSite validate_patch_site(const void *candidate) {
    // A ret within the first three instructions means the function is shorter
    // than the 16-byte entry patch.
    for (int i = 0; i < 3; ++i) {
        if (read_word(static_cast<const uint8_t *>(candidate) + 4 * i) ==
            kArm64RetOpcode)
            return PatchSite::kTooShort;
    }
    return has_interior_branch(candidate) ? PatchSite::kInteriorBranch
                                          : PatchSite::kOk;
}

// Most libil2cpp exports are a 4-byte jump-table thunk (`b imm26`) that is
// too small to instrument; decode the branch and instrument the real
// implementation instead. The chain does not stop at the first hop: several
// exports land on 8-byte tail-call adapters (`mov; b impl`) that sit directly
// in front of the shared implementation, so instrumenting the adapter
// overwrites the head of the following function (this corrupted Monitor::Enter
// via the il2cpp_monitor_enter/il2cpp_monitor_try_enter pair and SIGILL'd the
// process). Follow unconditional branches until a candidate with at least
// four straight-line instructions (16 bytes, Dobby's entry patch size) is
// found — but never follow a `b` that sits behind frame-setup instructions:
// that branch is part of a real body, not a thunk hop.
//
// If the resolved head cannot be patched because an outside branch targets
// its interior, try the call-wrapper pattern: il2cpp_object_new is
// `str x30,[sp,#-16]!; bl Object::New; ldr x30,[sp],#16; ret` plus an
// exception-cleanup path that branches back into the epilogue, so no 16-byte
// window at the export is patchable. The single bl leads to the real body,
// which is patchable and covers every caller of the wrapper.
void *resolve_instrument_target(void *export_address, int budget = 8) {
    void *candidate = export_address;
    for (int hop = 0; hop < budget; ++hop) {
        uint32_t words[4];
        std::memcpy(words, candidate, sizeof(words));
        int b_index = -1;
        for (int i = 0; i < 4; ++i) {
            if ((words[i] & kArm64BranchMask) == kArm64BranchOpcode) {
                b_index = i;
                break;
            }
        }
        if (b_index >= 0) {
            bool frame_before = false;
            for (int i = 0; i < b_index; ++i) {
                if (is_frame_setup(words[i])) {
                    frame_before = true;
                    break;
                }
            }
            if (!frame_before) {
                candidate = decode_branch_target(
                    reinterpret_cast<uint8_t *>(candidate) + 4 * b_index,
                    words[b_index]);
                continue;
            }
        }
        switch (validate_patch_site(candidate)) {
        case PatchSite::kOk:
            return candidate;
        case PatchSite::kTooShort:
            return nullptr;
        case PatchSite::kInteriorBranch: {
            void *body = single_bl_target(candidate);
            if (body == nullptr)
                return nullptr;
            LOGI("ThreadGuard: %#lx is a call-wrapper; guarding body %#lx",
                 (unsigned long)reinterpret_cast<uintptr_t>(candidate),
                 (unsigned long)reinterpret_cast<uintptr_t>(body));
            return resolve_instrument_target(body, budget - hop - 1);
        }
        }
    }
    return nullptr;
}

template <typename T>
bool resolve_symbol(void *handle, const char *name, T &destination, std::string &error) {
    destination = reinterpret_cast<T>(dlsym(handle, name));
    if (destination == nullptr) {
        error = std::string("ThreadGuard missing libil2cpp symbol: ") + name;
        return false;
    }
    return true;
}

bool install_locked(std::string &error) {
    void *handle = dlopen("libil2cpp.so", RTLD_NOW | RTLD_NOLOAD);
    if (handle == nullptr) {
        error = "ThreadGuard: libil2cpp.so is not loaded";
        return false;
    }

    if (!resolve_symbol(handle, "il2cpp_domain_get", p_domain_get, error) ||
        !resolve_symbol(handle, "il2cpp_thread_current", p_thread_current, error) ||
        !resolve_symbol(handle, "il2cpp_thread_attach", p_thread_attach, error))
        return false;

    g_domain = p_domain_get();
    if (g_domain == nullptr) {
        error = "ThreadGuard: il2cpp domain is not ready";
        return false;
    }

    if (pthread_key_create(&g_in_attach_key, nullptr) != 0) {
        error = "ThreadGuard: pthread_key_create (in-attach) failed";
        return false;
    }
    g_in_attach_key_ready = true;

    std::unordered_set<void *> instrumented;
    int installed = 0;
    int skipped = 0;
    bool ok = true;
    for (const GuardTarget &target : kGuardTargets) {
        void *export_address = dlsym(handle, target.symbol);
        if (export_address == nullptr) {
            LOGW("ThreadGuard: %s not exported", target.symbol);
            ++skipped;
            if (target.required) {
                error = std::string("ThreadGuard: required symbol missing: ") + target.symbol;
                ok = false;
            }
            continue;
        }
        void *impl = resolve_instrument_target(export_address);
        if (impl == nullptr) {
            LOGW("ThreadGuard: %s has no safely patchable implementation",
                 target.symbol);
            ++skipped;
            if (target.required) {
                error = std::string("ThreadGuard: unsafe instrument target for ") + target.symbol;
                ok = false;
            }
            continue;
        }
        if (!instrumented.insert(impl).second)
            continue;  // two exports resolve to the same implementation
        if (DobbyInstrument(impl, &guard_preamble) != 0) {
            LOGW("ThreadGuard: DobbyInstrument failed for %s (impl=%p)", target.symbol, impl);
            ++skipped;
            if (target.required) {
                error = std::string("ThreadGuard: instrument failed for ") + target.symbol;
                ok = false;
            }
            continue;
        }
        ++installed;
    }

    // Publish only after every instrument is in place so a preamble can never
    // observe a half-configured guard.
    g_ready.store(true, std::memory_order_release);
    LOGI("ThreadGuard installed: %d instrumented, %d skipped, domain=%p",
         installed, skipped, g_domain);
    return ok;
}

}  // namespace

bool install(std::string &error) {
    bool result = false;
    std::string once_error;
    std::call_once(g_install_once, [&] { result = install_locked(once_error); });
    if (!once_error.empty())
        error = once_error;
    return result;
}

}  // namespace starray::il2cpp_thread_guard

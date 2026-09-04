#ifndef STARRAY_DOBBY_HOOK_INTERNAL_H
#define STARRAY_DOBBY_HOOK_INTERNAL_H

#include <cstddef>
#include <cstdint>

#ifndef STARRAY_NATIVE_INTERNAL
#if defined(__GNUC__) && !defined(_WIN32)
#define STARRAY_NATIVE_INTERNAL __attribute__((visibility("hidden")))
#else
#define STARRAY_NATIVE_INTERNAL
#endif
#endif

extern "C" STARRAY_NATIVE_INTERNAL int
modmanager_hook_broker_install_protected(
    const char *owner,
    uint32_t module_id,
    uint32_t operation_id,
    uint32_t descriptor_slot,
    void *target,
    void *replacement,
    void **continuation_out);

namespace starray::code_patch {

// The caller must already hold native_patch::Transaction so page snapshots
// cannot race another physical Dobby write.
STARRAY_NATIVE_INTERNAL bool PrepareExternalWrite(void *address, size_t size);
STARRAY_NATIVE_INTERNAL void CommitExternalWrite(void *address, size_t size);
STARRAY_NATIVE_INTERNAL bool PrepareHookBrokerWrite(
    void *address,
    size_t size,
    bool authenticate_pristine);
STARRAY_NATIVE_INTERNAL void CommitHookBrokerWrite(void *address, size_t size);
STARRAY_NATIVE_INTERNAL int CopyAuthenticatedPristinePage(
    const void *page_base,
    size_t page_size,
    void *output,
    size_t output_size);

} // namespace starray::code_patch

#endif

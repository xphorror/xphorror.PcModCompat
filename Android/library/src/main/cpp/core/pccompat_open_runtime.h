#ifndef STARRAY_PCCOMPAT_OPEN_RUNTIME_H
#define STARRAY_PCCOMPAT_OPEN_RUNTIME_H

#include <stddef.h>
#include <stdint.h>

/*
 * Open-runtime helpers. These preserve pointer and generation contracts using
 * only the active runtime and MOD lifecycle state.
 */
static inline int pccompat_identity_address(uintptr_t address, uintptr_t *out) {
    if (address == (uintptr_t)0 || out == NULL)
        return 0;
    *out = address;
    return 1;
}

static inline int pccompat_identity_scalar(uint64_t value, uint64_t *out) {
    if (out == NULL)
        return 0;
    *out = value;
    return 1;
}

static inline int pccompat_runtime_enabled(uint64_t ignored) {
    (void)ignored;
    return 1;
}

static inline int pccompat_session_active(
    uint64_t session_handle,
    uint64_t host_generation,
    uint64_t resource_generation) {
    return session_handle != 0 && host_generation != 0 && resource_generation != 0;
}

static inline int pccompat_session_get_token(
    const char *mod_id,
    const uint8_t *assembly_digest,
    uint64_t *session_handle,
    uint64_t *host_generation,
    uint64_t *resource_generation) {
    (void)assembly_digest;
    if (mod_id == NULL || mod_id[0] == '\0' ||
        session_handle == NULL || host_generation == NULL ||
        resource_generation == NULL)
        return 0;
    *session_handle = 1;
    *host_generation = 1;
    *resource_generation = 1;
    return 1;
}

#define PC_COMPAT_RESOLVE_ADDRESS(module, operation, slot, flags, address, out) \
    pccompat_identity_address((uintptr_t)(address), (out))
#define PC_COMPAT_RESOLVE_CONTINUATION(module, operation, slot, address, out) \
    pccompat_identity_address((uintptr_t)(address), (out))
#define PC_COMPAT_RESOLVE_SCALAR(module, operation, slot, value, out) \
    pccompat_identity_scalar((uint64_t)(value), (out))

#endif

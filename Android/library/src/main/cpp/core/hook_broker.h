#pragma once

#include <stddef.h>
#include <stdint.h>

#include "modmanager_hook_broker_abi.h"

enum ModManagerHookAbiCompatibility {
    MODMANAGER_HOOK_ABI_NONE = 0,
    MODMANAGER_HOOK_ABI_CALCULATE_TICK_COLOR_WITHOUT_HIT_FLOOR = 1
};

enum ModManagerHookLayerFlags {
    MODMANAGER_HOOK_LAYER_FLAG_MANAGED_CALLBACK_GATE = 1u << 0
};

enum ModManagerPristinePageStatus {
    MODMANAGER_PRISTINE_PAGE_COPIED = 1,
    MODMANAGER_PRISTINE_PAGE_INVALID_ARGUMENT = -1,
    MODMANAGER_PRISTINE_PAGE_NOT_FOUND = -2,
    MODMANAGER_PRISTINE_PAGE_NOT_AUTHENTICATED = -3,
    MODMANAGER_PRISTINE_PAGE_PRISTINE_SIZE_MISMATCH = -4,
    MODMANAGER_PRISTINE_PAGE_EXPECTED_SIZE_MISMATCH = -5,
    MODMANAGER_PRISTINE_PAGE_CURRENT_MISMATCH = -6
};

#ifdef __cplusplus
extern "C" {
#endif

int modmanager_hook_broker_install(
    const char *owner,
    void *target,
    void *replacement,
    void **continuation_out);
int modmanager_hook_broker_install_compatible(
    const char *owner,
    void *target,
    void *replacement,
    int compatibility_kind,
    void **continuation_out);
int modmanager_hook_broker_install_generation(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    void **continuation_out);
int modmanager_hook_broker_install_generation_v2(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    uint32_t layer_flags,
    void **continuation_out);
int modmanager_hook_broker_install_compatible_generation(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    void **continuation_out);
int modmanager_hook_broker_install_compatible_generation_v2(
    const char *owner,
    uint64_t generation,
    void *target,
    void *replacement,
    int compatibility_kind,
    uint32_t layer_flags,
    void **continuation_out);

int modmanager_hook_broker_supports_owner_control(void);
int modmanager_hook_broker_set_owner_enabled(const char *owner, int enabled);
int modmanager_hook_broker_set_owner_generation_enabled(
    const char *owner,
    uint64_t generation,
    int enabled);
int modmanager_hook_broker_retire_owner_target(const char *owner, void *target);
int modmanager_hook_broker_retire_owner_generation_target(
    const char *owner,
    uint64_t generation,
    void *target);
int modmanager_hook_broker_retire_owner(const char *owner);
int modmanager_hook_broker_retire_owner_generation(
    const char *owner,
    uint64_t generation);
int modmanager_hook_broker_get_owner_retained_layer_count(const char *owner);
int modmanager_hook_broker_get_owner_generation_retained_layer_count(
    const char *owner,
    uint64_t generation);
int modmanager_hook_broker_get_owner_generation_untracked_callback_layer_count(
    const char *owner,
    uint64_t generation);
int modmanager_hook_broker_get_owner_enabled_layer_count(const char *owner);
int modmanager_hook_broker_get_chain_count(void);
int modmanager_hook_broker_get_layer_count(void *target);
int modmanager_hook_broker_copy_pristine_page(
    const void *page_base,
    size_t page_size,
    void *output,
    size_t output_size);
int modmanager_hook_broker_apply_authenticated_code_patch(
    const char *owner,
    void *target,
    const void *expected,
    const void *replacement,
    size_t size);
const AdoModManagerHookBrokerApiV1 *modmanager_hook_broker_get_api_v1(void);
const char *modmanager_hook_broker_get_last_error(void);

#ifdef __cplusplus
}
#endif

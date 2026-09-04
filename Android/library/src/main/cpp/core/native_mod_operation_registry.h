#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

int modmanager_native_operation_host_open_generation(
    const char *owner,
    uint64_t generation);
int modmanager_native_operation_host_cancel_generation_and_wait(
    const char *owner,
    uint64_t generation,
    uint32_t timeout_ms);
int modmanager_native_operation_host_resume_generation(
    const char *owner,
    uint64_t generation);
int modmanager_native_operation_host_retire_generation(
    const char *owner,
    uint64_t generation);
int modmanager_native_operation_host_get_active_count(
    const char *owner,
    uint64_t generation);

#ifdef __cplusplus
}
#endif

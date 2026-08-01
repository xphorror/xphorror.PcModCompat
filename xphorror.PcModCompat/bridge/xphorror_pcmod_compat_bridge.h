#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define XPHORROR_PCMOD_COMPAT_BRIDGE_ABI_VERSION 1u
#define XPHORROR_PCMOD_COMPAT_MAX_HIT_MARGINS 16u
#define XPHORROR_PCMOD_COMPAT_MAX_SCENE_NAME 128u

typedef enum xphorror_pcmod_compat_patch_status {
    XPHORROR_PCMOD_COMPAT_PATCH_REGISTERED_ONLY = 0,
    XPHORROR_PCMOD_COMPAT_PATCH_SUPPORTED = 1,
    XPHORROR_PCMOD_COMPAT_PATCH_UNSUPPORTED = 2
} xphorror_pcmod_compat_patch_status;

typedef struct xphorror_pcmod_compat_game_snapshot_v1 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t hit_margins_count_len;
    int32_t hit_margins_count[XPHORROR_PCMOD_COMPAT_MAX_HIT_MARGINS];
    double planet_speed;
    float percent_acc;
    float percent_x_acc;
    int32_t player_count;
    char scene_name_utf8[XPHORROR_PCMOD_COMPAT_MAX_SCENE_NAME];
} xphorror_pcmod_compat_game_snapshot_v1;

typedef int32_t (*xphorror_pcmod_compat_publish_snapshot_fn)(
    const xphorror_pcmod_compat_game_snapshot_v1* snapshot);

typedef int32_t (*xphorror_pcmod_compat_update_patch_status_fn)(
    const char* mod_id_utf8,
    const char* callback_type_utf8,
    const char* callback_method_utf8,
    xphorror_pcmod_compat_patch_status status,
    const char* reason_utf8);

typedef int32_t (*xphorror_pcmod_compat_consume_scene_request_fn)(
    char* out_scene_name_utf8,
    size_t out_scene_name_capacity);

#ifdef __cplusplus
}
#endif

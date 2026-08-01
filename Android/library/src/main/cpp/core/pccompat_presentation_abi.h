#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum {
    PC_COMPAT_PRESENTATION_ABI_VERSION = 1u,
    PC_COMPAT_PRESENTATION_MAX_COMMANDS = 64u,
};

// Fixed command catalog.  Commands carry only bounded scalar payloads; text
// values are referenced by a recipe-owned initialization slot rather than a
// native pointer.  Unknown values must be ignored by the UnityMain sink.
enum PcCompatPresentationCommandTypeV1 {
    PC_COMPAT_PRESENTATION_ENSURE_GRAPH = 1u,
    PC_COMPAT_PRESENTATION_SET_ACTIVE = 2u,
    PC_COMPAT_PRESENTATION_SET_RECT = 3u,
    PC_COMPAT_PRESENTATION_SET_TEXT = 4u,
    PC_COMPAT_PRESENTATION_SET_COLOR = 5u,
    PC_COMPAT_PRESENTATION_SET_FONT_SIZE = 6u,
    PC_COMPAT_PRESENTATION_DESTROY_GRAPH = 7u,
    PC_COMPAT_PRESENTATION_INVALIDATE_TARGET = 8u,
};

#pragma pack(push, 4)
typedef struct PcCompatPresentationCommandV1 {
    uint64_t sequence;
    uint32_t session_generation;
    uint32_t generation;
    uint32_t rule_id;
    uint32_t command_type;
    uint32_t target_id;
    uint32_t reserved;
    int64_t payload0;
    int64_t payload1;
    float value0;
    float value1;
} PcCompatPresentationCommandV1;

typedef struct PcCompatPresentationSnapshotV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t publication_generation;
    uint32_t session_generation;
    uint32_t available;
    uint32_t command_count;
    uint32_t reserved;
    uint64_t dropped_stale_tasks;
    uint64_t scheduler_overflow_count;
    int64_t published_raw_ns;
    PcCompatPresentationCommandV1 commands[PC_COMPAT_PRESENTATION_MAX_COMMANDS];
} PcCompatPresentationSnapshotV1;
#pragma pack(pop)

int modmanager_pccompat_read_presentation_snapshot(
    void *output,
    uint32_t output_size);

#ifdef __cplusplus
}
#endif

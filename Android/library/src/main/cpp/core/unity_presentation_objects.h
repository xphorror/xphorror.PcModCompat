#pragma once

#include "hud_deadline_scheduler.h"
#include "pccompat_recipe_binary.h"

#include <cstdint>
#include <string>
#include <vector>

namespace starray::unity_presentation_objects {

struct ObjectStats {
    uint32_t registered_graphs = 0;
    uint32_t materialized_graphs = 0;
    uint32_t materialization_failures = 0;
    uint32_t invalid_target_count = 0;
    uint32_t retired_graphs = 0;
    uint32_t unsupported_command_count = 0;
};

struct SnapshotRangeConsumeResult {
    uint32_t consumed_commands = 0;
    bool deferred = false;
};

bool register_bundle_graph(
    uint32_t bundle_id,
    const std::string &mod_id,
    const std::vector<pccompat_recipe::UiObjectNode> &nodes,
    const std::vector<pccompat_recipe::UiResourceBinding> &resources,
    std::string &error);
void discard_bundle_graph(uint32_t bundle_id);
bool set_bundle_presentation_enabled(uint32_t bundle_id, bool enabled);
void clear_bundle_graphs();

// Must be called on UnityMain from the PresentationSink hook.  The worker
// only publishes immutable scalar commands; no Unity API is touched here
// until this function is entered from the Unity render callback.
void consume_snapshot(const hud_logic::PresentationSnapshot &snapshot);
SnapshotRangeConsumeResult consume_snapshot_range(
    const hud_logic::PresentationSnapshot &snapshot,
    uint32_t start_index,
    uint32_t count,
    bool resolve_resources);
bool has_pending_retirements();
void drain_retired_on_unity_main();
void invalidate_all_runtime_graphs_on_unity_main();
void set_resource_resolver_callback(void *callback);
void drain_pending_resources_on_unity_main();
void refresh_resources_on_unity_main();
void clear_resources_for_mod_on_unity_main(const std::string &mod_id);

ObjectStats read_stats();

}  // namespace starray::unity_presentation_objects

#pragma once

#include "pccompat_recipe_binary.h"

#include <cstdint>
#include <string>

namespace starray::unity_presentation_sink {

struct SinkStats {
    uint32_t installed = 0;
    uint32_t primary_hook = 0;
    uint32_t fallback_hook = 0;
    uint32_t consume_opportunities = 0;
    uint32_t snapshot_updates = 0;
    uint32_t command_count = 0;
    uint32_t unsupported_command_count = 0;
    uint32_t last_publication_generation = 0;
    uint32_t last_session_generation = 0;
    uint32_t registered_graph_count = 0;
    uint32_t materialized_graph_count = 0;
    uint32_t graph_materialization_failures = 0;
    uint32_t invalid_target_count = 0;
    uint32_t retired_graph_count = 0;
    uint64_t presentation_history_overflow_count = 0;
    uint32_t stream_gap_count = 0;
    uint32_t stream_faulted = 0;
    uint32_t ongui_hook = 0;
    uint32_t ongui_process_hook = 0;
    uint32_t ongui_begin_hook = 0;
    uint32_t ongui_enabled = 0;
    uint32_t ongui_process_event_count = 0;
    uint32_t ongui_begin_gui_count = 0;
    uint32_t ongui_dispatch_count = 0;
};

#pragma pack(push, 4)
struct PcCompatPresentationSinkStatsV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t installed;
    uint32_t primary_hook;
    uint32_t fallback_hook;
    uint32_t consume_opportunities;
    uint32_t snapshot_updates;
    uint32_t command_count;
    uint32_t unsupported_command_count;
    uint32_t last_publication_generation;
    uint32_t last_session_generation;
};

struct PcCompatPresentationSinkStatsV2 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t installed;
    uint32_t primary_hook;
    uint32_t fallback_hook;
    uint32_t consume_opportunities;
    uint32_t snapshot_updates;
    uint32_t command_count;
    uint32_t unsupported_command_count;
    uint32_t last_publication_generation;
    uint32_t last_session_generation;
    uint32_t registered_graph_count;
    uint32_t materialized_graph_count;
    uint32_t graph_materialization_failures;
    uint32_t invalid_target_count;
    uint32_t retired_graph_count;
};

struct PcCompatPresentationSinkStatsV3 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t installed;
    uint32_t primary_hook;
    uint32_t fallback_hook;
    uint32_t consume_opportunities;
    uint32_t snapshot_updates;
    uint32_t command_count;
    uint32_t unsupported_command_count;
    uint32_t last_publication_generation;
    uint32_t last_session_generation;
    uint32_t registered_graph_count;
    uint32_t materialized_graph_count;
    uint32_t graph_materialization_failures;
    uint32_t invalid_target_count;
    uint32_t retired_graph_count;
    uint64_t presentation_history_overflow_count;
    uint32_t stream_gap_count;
    uint32_t stream_faulted;
};

struct PcCompatPresentationSinkStatsV4 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t installed;
    uint32_t primary_hook;
    uint32_t fallback_hook;
    uint32_t consume_opportunities;
    uint32_t snapshot_updates;
    uint32_t command_count;
    uint32_t unsupported_command_count;
    uint32_t last_publication_generation;
    uint32_t last_session_generation;
    uint32_t registered_graph_count;
    uint32_t materialized_graph_count;
    uint32_t graph_materialization_failures;
    uint32_t invalid_target_count;
    uint32_t retired_graph_count;
    uint64_t presentation_history_overflow_count;
    uint32_t stream_gap_count;
    uint32_t stream_faulted;
    uint32_t ongui_hook;
    uint32_t ongui_process_hook;
    uint32_t ongui_begin_hook;
    uint32_t ongui_enabled;
    uint32_t ongui_process_event_count;
    uint32_t ongui_begin_gui_count;
    uint32_t ongui_dispatch_count;
};
#pragma pack(pop)

constexpr uint32_t kPresentationSinkStatsAbiVersionV1 = 1;
constexpr uint32_t kPresentationSinkStatsAbiVersionV2 = 2;
constexpr uint32_t kPresentationSinkStatsAbiVersionV3 = 3;
constexpr uint32_t kPresentationSinkStatsAbiVersionV4 = 4;

extern "C" int modmanager_pccompat_read_presentation_sink_stats(
    void *output,
    uint32_t output_size);

// Low-frequency managed control work is armed explicitly and drained from the
// same metadata-resolved UnityMain hook as presentation commands.
extern "C" void modmanager_pccompat_set_unity_main_work_callback(void *callback);
extern "C" int modmanager_pccompat_ensure_presentation_sink();
extern "C" void modmanager_pccompat_request_presentation_sink_install();
extern "C" int modmanager_pccompat_is_presentation_sink_installed();
extern "C" int modmanager_pccompat_request_unity_main_work();

// Unity 6 IMGUI converts GUIStyle.font through TextSettings. These low-frequency
// owner resource calls publish and retire the matching reconstructed TextCore
// FontAsset while the permanent HookBroker entry remains installed.
extern "C" int modmanager_pccompat_register_imgui_font_mapping(
    void *font,
    void *text_core_font_asset);
extern "C" int modmanager_pccompat_unregister_imgui_font_mapping(void *font);

// Managed self-render frames share the permanent UnityMain presentation hook,
// but remain separately gated so a process with no active managed HUD has no
// per-frame CoreCLR transition.
extern "C" void modmanager_pccompat_set_managed_frame_callback(void *callback);
extern "C" void modmanager_pccompat_set_managed_frame_enabled(int enabled);
extern "C" void modmanager_pccompat_set_managed_frame_mode(int mode);

// Managed OnGUI dispatch is invoked from the GUIUtility.ProcessEvent hook so
// mod components observe a valid IMGUI context and the real Event.current.
// The callback fires per IMGUI event whenever managed presentation is active.
extern "C" void modmanager_pccompat_set_managed_ongui_callback(void *callback);
extern "C" void modmanager_pccompat_set_managed_ongui_enabled(int enabled);

// Installs the metadata-resolved UnityMain presentation opportunity hook.
// It is idempotent and never unhooks; a failure is reported for coordinator
// diagnostics and may be retried on a later metadata pass.
bool ensure_installed(std::string &error);

bool register_bundle_graph(
    uint32_t bundle_id,
    const std::string &mod_id,
    const std::vector<pccompat_recipe::UiObjectNode> &nodes,
    const std::vector<pccompat_recipe::UiResourceBinding> &resources,
    std::string &error);
void discard_bundle_graph(uint32_t bundle_id);
bool set_bundle_presentation_enabled(uint32_t bundle_id, bool enabled);
void clear_bundle_graphs();

SinkStats read_stats();

}  // namespace starray::unity_presentation_sink

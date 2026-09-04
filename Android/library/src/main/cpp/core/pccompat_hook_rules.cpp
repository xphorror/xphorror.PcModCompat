#include <android/log.h>
#include "pccompat_open_runtime.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <elf.h>
#include <fstream>
#include <iterator>
#include <link.h>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <new>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <sys/mman.h>
#include <unistd.h>

#include "hook_broker.h"
#include "async_input_observer_bridge.h"
#include "dobby_hook_internal.h"
#include "hud_logic_worker.h"
#include "il2cpp_foreign_thread_guard.h"
#include "native_rule_vm.h"
#include "pccompat_recipe_binary.h"
#include "pccompat_metadata_resolver.h"
#include "realtime_event_core.h"
#include "unity_presentation_sink.h"
#include "ui_recipe_lifecycle_runtime.h"

#define LOG_TAG "StArray.PcCompatRules"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

extern "C" {
int modmanager_pccompat_resolve_pending_slots();
int modmanager_pccompat_prepare_install_plan();
int modmanager_pccompat_install_planned_slots();
void modmanager_pccompat_start_hook_coordinator();
}

namespace {

constexpr uint32_t kApplicationFocusKnown = 1u << 0;
constexpr uint32_t kApplicationResumed = 1u << 1;
constexpr uint32_t kApplicationWindowFocused = 1u << 2;
std::atomic<uint32_t> g_application_focus_state{0};

struct RuntimeRule {
    std::string id;
    std::string feature_id;
    std::string stage;
    int stage_code = -1;
    std::string op;
    int op_code = -1;
    uint64_t required_capabilities = 0;
    bool default_enabled = true;
    std::string source;
};

struct RuntimeTarget {
    int id = 0;
    std::string assembly_name;
    std::string namespace_name;
    std::string type_name;
    std::string method_name;
    bool is_static = false;
    int generic_arity = 0;
    std::string return_type;
    std::vector<std::string> parameter_types;
    bool has_param_count = false;
    int param_count = -1;
    std::string abi_kind;
    std::vector<RuntimeRule> rules;
    bool resolve_attempted = false;
    bool resolved = false;
    std::string resolve_error;
    void *klass = nullptr;
    void *method = nullptr;
    void *function = nullptr;
};

struct Il2CppMethodInfoHead {
    void *method_pointer = nullptr;
};

struct Il2CppMetadataApi {
    using DomainGetFn = void *(*)();
    using GetCorlibFn = void *(*)();
    using ThreadAttachFn = void *(*)(void *);
    using DomainGetAssembliesFn = const void **(*)(void *, size_t *);
    using AssemblyGetImageFn = void *(*)(const void *);
    using ImageGetNameFn = const char *(*)(void *);
    using ClassFromNameFn = void *(*)(void *, const char *, const char *);
    using ObjectGetClassFn = void *(*)(void *);
    using ClassGetTypeFn = const void *(*)(void *);
    using TypeGetObjectFn = void *(*)(const void *);
    using ClassGetFieldFromNameFn = void *(*)(void *, const char *);
    using ClassGetMethodsFn = const void *(*)(void *, void **);
    using FieldGetOffsetFn = size_t (*)(void *);
    using FieldStaticGetValueFn = void (*)(void *, void *);
    using MethodGetNameFn = const char *(*)(const void *);
    using MethodGetParamCountFn = uint32_t (*)(const void *);
    using MethodGetParamFn = const void *(*)(const void *, uint32_t);
    using MethodGetReturnTypeFn = const void *(*)(const void *);
    using MethodGetFlagsFn = uint32_t (*)(const void *, uint32_t *);
    using MethodIsGenericFn = bool (*)(const void *);
    using TypeGetNameFn = char *(*)(const void *);
    using RuntimeInvokeFn = void *(*)(const void *, void *, void **, void **);
    using ObjectUnboxFn = void *(*)(void *);
    using StringNewFn = void *(*)(const char *);
    using StringCharsFn = const char16_t *(*)(void *);
    using StringLengthFn = int32_t (*)(void *);
    using ArrayObjectHeaderSizeFn = uint32_t (*)();
    using ArrayLengthFn = uintptr_t (*)(void *);
    using ObjectNewFn = void *(*)(void *);
    using ArrayNewFn = void *(*)(void *, uintptr_t);
    // Unity 6 changed the il2cpp GCHandle ABI from a 32-bit index to a 64-bit
    // tagged slot pointer (Il2CppGCHandle == void*). Declaring these as uint32_t
    // truncates the returned handle on AArch64 and il2cpp_gchandle_free then
    // dereferences the truncated value and segfaults. Using void* is compatible
    // with both ABIs: old 32-bit index values zero-extend cleanly.
    using GcHandleNewFn = void *(*)(void *, bool);
    using GcHandleGetTargetFn = void *(*)(void *);
    using GcHandleFreeFn = void (*)(void *);
    using FreeFn = void (*)(void *);

    void *handle = nullptr;
    DomainGetFn domain_get = nullptr;
    GetCorlibFn get_corlib = nullptr;
    ThreadAttachFn thread_attach = nullptr;
    DomainGetAssembliesFn domain_get_assemblies = nullptr;
    AssemblyGetImageFn assembly_get_image = nullptr;
    ImageGetNameFn image_get_name = nullptr;
    ClassFromNameFn class_from_name = nullptr;
    ObjectGetClassFn object_get_class = nullptr;
    ClassGetTypeFn class_get_type = nullptr;
    TypeGetObjectFn type_get_object = nullptr;
    ClassGetFieldFromNameFn class_get_field_from_name = nullptr;
    ClassGetMethodsFn class_get_methods = nullptr;
    FieldGetOffsetFn field_get_offset = nullptr;
    FieldStaticGetValueFn field_static_get_value = nullptr;
    MethodGetNameFn method_get_name = nullptr;
    MethodGetParamCountFn method_get_param_count = nullptr;
    MethodGetParamFn method_get_param = nullptr;
    MethodGetReturnTypeFn method_get_return_type = nullptr;
    MethodGetFlagsFn method_get_flags = nullptr;
    MethodIsGenericFn method_is_generic = nullptr;
    TypeGetNameFn type_get_name = nullptr;
    RuntimeInvokeFn runtime_invoke = nullptr;
    ObjectUnboxFn object_unbox = nullptr;
    StringNewFn string_new = nullptr;
    StringCharsFn string_chars = nullptr;
    StringLengthFn string_length = nullptr;
    ArrayObjectHeaderSizeFn array_object_header_size = nullptr;
    ArrayLengthFn array_length = nullptr;
    ObjectNewFn object_new = nullptr;
    ArrayNewFn array_new = nullptr;
    GcHandleNewFn gchandle_new = nullptr;
    GcHandleGetTargetFn gchandle_get_target = nullptr;
    GcHandleFreeFn gchandle_free = nullptr;
    FreeFn free_memory = nullptr;
    void *domain = nullptr;
    std::map<std::string, void *> images;
    bool ready = false;
};

struct ResolvedMethodMetadata {
    const void *method_info = nullptr;
    void *function = nullptr;
    std::string name;
    std::string return_type;
    std::vector<std::string> params;
    bool is_static = false;
    bool is_generic = false;
};

struct PcModSessionToken {
    uint64_t session_handle = 0;
    uint64_t host_generation = 0;
    uint64_t resource_generation = 0;

    bool operator==(const PcModSessionToken &other) const {
        return session_handle == other.session_handle &&
               host_generation == other.host_generation &&
               resource_generation == other.resource_generation;
    }
};

struct RuntimeBundle {
    uint32_t bundle_id = 0;
    std::string path;
    std::string mod_id;
    std::string recipe_id;
    std::string compatibility;
    bool recipe_presentation_enabled = true;
    uint64_t required_capabilities = 0;
    PcModSessionToken pc_mod_session;
    std::vector<RuntimeTarget> targets;
    std::vector<starray::pccompat_recipe::UiObjectNode> ui_objects;
    std::vector<starray::pccompat_recipe::UiResourceBinding> ui_resources;
    std::vector<starray::rule_vm::Instruction> ui_bytecode;
    std::vector<starray::pccompat_recipe::LifecycleProgram> ui_lifecycle_programs;
};

enum HookSlotState : int {
    SlotPendingResolve = 0,
    SlotResolved = 1,
    SlotHookInstalled = 2,
    SlotInstallFailed = 3,
    SlotDisabledByCapability = 4,
    SlotFaulted = 5,
    SlotSkippedKnownConflict = 6,
};

struct HookSlotRuleRef {
    uint32_t bundle_id = 0;
    int target_id = 0;
    PcModSessionToken pc_mod_session;
    std::string rule_id;
    std::string feature_id;
    int stage_code = -1;
    int op_code = -1;
    uint64_t required_capabilities = 0;
    bool enabled = true;
    std::string disabled_reason;
    uint32_t managed_event_id = 0;
    int managed_event_priority = 400;
    uint64_t managed_event_registration_index = 0;
    std::string managed_event_owner;
    std::vector<std::string> managed_event_before;
    std::vector<std::string> managed_event_after;
    uint32_t managed_prefix_id = 0;
    int managed_prefix_priority = 400;
    uint64_t managed_prefix_registration_index = 0;
    std::string managed_prefix_owner;
    std::vector<std::string> managed_prefix_before;
    std::vector<std::string> managed_prefix_after;
};

struct ManagedPrefixOrderMetadata {
    int priority = 400;
    uint64_t registration_index = 0;
    std::string owner;
    std::vector<std::string> before;
    std::vector<std::string> after;
};

struct HookSlot {
    uint32_t slot_id = 0;
    std::string key;
    std::string assembly_name;
    std::string namespace_name;
    std::string type_name;
    std::string method_name;
    bool is_static = false;
    int generic_arity = 0;
    std::string return_type;
    std::vector<std::string> parameter_types;
    bool has_param_count = false;
    int param_count = -1;
    std::string abi_kind;
    HookSlotState state = SlotPendingResolve;
    bool resolve_attempted = false;
    bool resolve_failed = false;
    void *function = nullptr;
    void *original = nullptr;
    int dispatcher_index = -1;
    bool install_planned = false;
    bool install_blocked = false;
    std::string status;
    std::vector<HookSlotRuleRef> before_rules;
    std::vector<HookSlotRuleRef> replace_rules;
    std::vector<HookSlotRuleRef> after_rules;
};

struct OverlayRuntimeState {
    std::atomic<uint32_t> generation{1};
    std::atomic<uint32_t> session_epoch{0};
    std::atomic<uint32_t> visible{0};
    std::atomic<uint32_t> practice{0};
    std::atomic<uint32_t> show_count{0};
    std::atomic<uint32_t> hide_count{0};
    std::atomic<uint32_t> player_update_count{0};
    std::atomic<uint32_t> state_change_count{0};
    std::atomic<uint32_t> last_op{0xFFFFFFFFu};
    std::atomic<uint32_t> last_target_kind{0};
    std::atomic<int32_t> player_count{0};
    std::atomic<int32_t> last_seq_id{0};
    std::atomic<uint32_t> last_is_restart{0};
    std::atomic<int32_t> last_wipe_direction{0};
    std::atomic<uint32_t> last_reset_to_editor{0};
    std::atomic<uint32_t> judgement_hit_count{0};
    std::atomic<uint32_t> judgement_reset_count{0};
    std::atomic<int32_t> last_hit_margin{0};
    std::atomic<uint32_t> floor_move_count{0};
    std::atomic<uint32_t> last_floor_exit_angle_bits{0};
    std::atomic<int32_t> last_floor_move_hit_margin{0};
    std::atomic<uint32_t> player_hit_count{0};
    std::atomic<uint32_t> last_player_hit_is_auto{0};
    std::atomic<uint32_t> death_count{0};
    std::atomic<uint32_t> last_death_overload{0};
    std::atomic<uint32_t> last_death_multipress{0};
    std::atomic<uint32_t> last_death_hitbox{0};
    std::atomic<uint32_t> hit_timing_count{0};
    std::atomic<uint32_t> last_hit_timing_ms_bits{0};
    std::atomic<int32_t> last_hit_timing_margin{0};
    std::atomic<uint32_t> accuracy_snapshot_count{0};
    std::atomic<uint32_t> percent_acc_bits{0};
    std::atomic<uint32_t> percent_x_acc_bits{0};
    std::atomic<uint32_t> progress_bits{0};
    std::atomic<int32_t> combo_count{0};
    std::atomic<uint32_t> attempt_count{0};
    std::atomic<uint32_t> bpm_snapshot_count{0};
    std::atomic<uint32_t> tile_bpm_bits{0};
    std::atomic<uint32_t> kps_bits{0};
    std::atomic<uint32_t> timeline_snapshot_count{0};
    std::atomic<uint32_t> music_time_bits{0};
    std::atomic<uint32_t> music_total_time_bits{0};
    std::atomic<uint32_t> map_time_bits{0};
    std::atomic<uint32_t> map_total_time_bits{0};
    std::atomic<int32_t> checkpoints_used{0};
    std::atomic<int32_t> current_checkpoint{0};
    std::atomic<int32_t> total_checkpoints{0};
    std::atomic<int32_t> current_seq_id{0};
    std::atomic<int32_t> floor_count{0};
    std::atomic<uint32_t> start_progress_bits{0};
    std::atomic<uint32_t> speed_multiplier_bits{0};
    std::atomic<uint32_t> planet_speed_bits{0};
    std::atomic<uint32_t> session_auto{0};
    std::atomic<uint32_t> rdc_auto{0};
    std::atomic<uint32_t> no_fail{0};
    std::atomic<uint32_t> paused{0};
    std::atomic<uint32_t> is_game_world{0};
    std::atomic<uint32_t> song_pitch_bits{0};
    std::atomic<uint64_t> conductor_add_offset_bits{0};
    std::atomic<uint64_t> conductor_songposition_minusi_bits{0};
    std::atomic<uint32_t> is_scn_game{0};
    std::atomic<uint32_t> game_ready{0};
    std::atomic<uint32_t> input_state_generation{0};
    std::atomic<uint32_t> input_held_mask{0};
    std::atomic<uint32_t> input_last_down_mask{0};
    std::atomic<uint32_t> input_last_up_mask{0};
    std::atomic<uint32_t> input_total_count{0};
    std::atomic<uint32_t> input_kps_bits{0};
    std::atomic<uint64_t> valid_game_snapshot_fields{0};
    std::atomic<uintptr_t> controller_pointer{0};
    std::atomic<uintptr_t> conductor_pointer{0};
    std::atomic<uintptr_t> level_maker_pointer{0};
    std::atomic<uintptr_t> current_floor_pointer{0};
    std::atomic<uintptr_t> first_floor_pointer{0};
    std::atomic<uintptr_t> song_pointer{0};
    std::atomic<uintptr_t> planetary_system_pointer{0};
};

struct OwnerOverlaySession {
    std::string mod_id;
    uint64_t session_generation = 0;
    OverlayRuntimeState state;
    std::atomic<uint32_t> retired{0};
    std::atomic<int64_t> last_timeline_poll_ms{0};
    std::atomic<uint32_t> last_published_input_generation{0};
    std::atomic<int64_t> last_margin_callback_ms{0};
};

#pragma pack(push, 4)
struct PcCompatOverlaySnapshotV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t generation;
    uint32_t visible;
    uint32_t practice;
    uint32_t show_count;
    uint32_t hide_count;
    uint32_t player_update_count;
    uint32_t state_change_count;
    int32_t last_op;
    int32_t last_target_kind;
    int32_t player_count;
    int32_t last_seq_id;
    uint32_t last_is_restart;
    int32_t last_wipe_direction;
    uint32_t last_reset_to_editor;
    uint32_t judgement_hit_count;
    uint32_t judgement_reset_count;
    int32_t last_hit_margin;
    uint32_t floor_move_count;
    float last_floor_exit_angle;
    int32_t last_floor_move_hit_margin;
    uint32_t player_hit_count;
    uint32_t last_player_hit_is_auto;
    uint32_t death_count;
    uint32_t last_death_overload;
    uint32_t last_death_multipress;
    uint32_t last_death_hitbox;
    uint32_t hit_timing_count;
    float last_hit_timing_ms;
    int32_t last_hit_timing_margin;
    uint32_t accuracy_snapshot_count;
    float percent_acc;
    float percent_x_acc;
    float progress;
    int32_t combo_count;
    uint32_t attempt_count;
    uint32_t bpm_snapshot_count;
    float tile_bpm;
    float kps;
    uint32_t timeline_snapshot_count;
    float music_time;
    float music_total_time;
    float map_time;
    float map_total_time;
    int32_t checkpoints_used;
    int32_t current_checkpoint;
    int32_t total_checkpoints;
    int32_t current_seq_id;
    int32_t floor_count;
    float start_progress;
    float speed_multiplier;
    uint32_t session_auto;
    uint32_t input_state_generation;
    uint32_t input_held_mask;
    uint32_t input_last_down_mask;
    uint32_t input_last_up_mask;
    uint32_t input_total_count;
    float input_kps;
    float planet_speed;
    uint32_t rdc_auto;
    uint32_t no_fail;
    uint32_t paused;
    uint32_t is_game_world;
    float song_pitch;
    double conductor_add_offset;
    double conductor_songposition_minusi;
    uint32_t is_scn_game;
    uint32_t game_ready;
    uint32_t session_epoch;
    uint64_t valid_game_snapshot_fields;
    uint64_t controller_pointer;
    uint64_t conductor_pointer;
    uint64_t level_maker_pointer;
    uint64_t current_floor_pointer;
    uint64_t first_floor_pointer;
    uint64_t song_pointer;
    uint64_t planetary_system_pointer;
};
#pragma pack(pop)

constexpr uint32_t kOverlaySnapshotAbiVersionV2 = 2;
constexpr uint32_t kOverlaySnapshotV2Size = 160;
constexpr uint32_t kOverlaySnapshotAbiVersionV3 = 3;
constexpr uint32_t kOverlaySnapshotV3Size = 236;
constexpr uint32_t kOverlaySnapshotAbiVersionV4 = 4;
constexpr uint32_t kOverlaySnapshotV4Size = 240;
constexpr uint32_t kOverlaySnapshotAbiVersionV5 = 5;
constexpr uint32_t kOverlaySnapshotV5Size = 284;
constexpr uint32_t kOverlaySnapshotAbiVersionV6 = 6;
constexpr uint32_t kOverlaySnapshotV6Size = 288;
constexpr uint32_t kOverlaySnapshotAbiVersion = 7;
static_assert(sizeof(PcCompatOverlaySnapshotV1) == 352);
static_assert(offsetof(PcCompatOverlaySnapshotV1, timeline_snapshot_count) == kOverlaySnapshotV2Size);
static_assert(offsetof(PcCompatOverlaySnapshotV1, planet_speed) == kOverlaySnapshotV3Size);
static_assert(offsetof(PcCompatOverlaySnapshotV1, rdc_auto) == kOverlaySnapshotV4Size);
static_assert(offsetof(PcCompatOverlaySnapshotV1, valid_game_snapshot_fields) == kOverlaySnapshotV6Size);

constexpr uint64_t kGameSnapshotProgress = 1ULL << 0;
constexpr uint64_t kGameSnapshotCurrentSeqId = 1ULL << 1;
constexpr uint64_t kGameSnapshotCheckpoints = 1ULL << 2;
constexpr uint64_t kGameSnapshotFloor = 1ULL << 3;
constexpr uint64_t kGameSnapshotAccuracy = 1ULL << 4;
constexpr uint64_t kGameSnapshotBpm = 1ULL << 5;
constexpr uint64_t kGameSnapshotTimeline = 1ULL << 6;
constexpr uint64_t kGameSnapshotPlanetSpeed = 1ULL << 7;
constexpr uint64_t kGameSnapshotState = 1ULL << 8;
constexpr uint64_t kGameSnapshotConductor = 1ULL << 9;
constexpr uint64_t kGameSnapshotSongPitch = 1ULL << 10;
constexpr uint64_t kGameSnapshotPlayer = 1ULL << 11;

#pragma pack(push, 4)
struct PcCompatInputHudSnapshotV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t publication_generation;
    uint32_t session_generation;
    uint32_t source_generation;
    uint32_t touch_lane_count;
    uint32_t touch_lane_held_mask;
    uint32_t touch_lane_last_down_mask;
    uint32_t touch_lane_last_up_mask;
    uint32_t input_total_count;
    uint32_t keyboard_held_count;
    float input_kps;
    uint64_t source_sequence;
    uint64_t dropped_event_count;
    int64_t completed_raw_ns;
    int64_t session_anchor_raw_ns;
    uint16_t touch_lane_held_counts[starray::hud_logic::kMaxTouchLanes];
    uint16_t reserved[2];
    uint32_t touch_lane_total_counts[starray::hud_logic::kMaxTouchLanes];
    int64_t touch_lane_last_down_raw_ns[starray::hud_logic::kMaxTouchLanes];
    int64_t touch_lane_last_up_raw_ns[starray::hud_logic::kMaxTouchLanes];
};

struct PcCompatClockAnchorSnapshotV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t publication_generation;
    uint32_t session_generation;
    uint32_t valid_mask;
    int32_t frame_count;
    float unity_time_scale;
    float audio_position_seconds;
    double unity_scaled_seconds;
    double song_position_seconds;
    double map_position_seconds;
    int64_t monotonic_raw_ns;
};

#pragma pack(push, 1)
struct PcCompatRawInputEventV1 {
    uint64_t sequence;
    int64_t raw_ns;
    uint32_t state_generation;
    uint32_t session_generation;
    uint32_t producer_epoch;
    uint8_t producer;
    uint8_t source;
    uint8_t phase;
    uint8_t reserved0;
    int32_t code;
    int32_t slot;
    int32_t pointer_count;
    int32_t scan_code;
    int32_t meta_state;
    int32_t device_id;
    int32_t repeat_count;
    int32_t android_flags;
    int32_t source_code;
    int32_t viewport_width;
    int32_t viewport_height;
    float x;
    float y;
    uint32_t flags;
};

struct PcCompatRawInputReadV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t cursor;
    uint64_t dropped_before_cursor;
    uint32_t count;
    uint32_t capacity;
};
#pragma pack(pop)

struct PcCompatVmFaultSnapshotV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t cursor;
    uint64_t sequence;
    int64_t timestamp_ns;
    uint32_t rule_id;
    uint32_t code;
    uint32_t pc;
    uint32_t opcode;
    uint32_t count;
    uint64_t dropped_before_cursor;
    char message[160];
};
#pragma pack(pop)

constexpr uint32_t kInputHudSnapshotAbiVersion = 1;
static_assert(sizeof(PcCompatInputHudSnapshotV1) == 304);
static_assert(sizeof(starray::realtime::LegacyInputSnapshot) == 8288);
static_assert(sizeof(PcCompatRawInputEventV1) == 88);
static_assert(sizeof(PcCompatRawInputReadV1) == 32);
constexpr uint32_t kRawInputReadAbiVersion = 1;
constexpr uint32_t kClockAnchorSnapshotAbiVersion = 1;
static_assert(sizeof(PcCompatClockAnchorSnapshotV1) == 64);
constexpr uint32_t kVmFaultSnapshotAbiVersion = 1;
static_assert(sizeof(PcCompatVmFaultSnapshotV1) == 220);

struct RuleState {
    std::vector<RuntimeBundle> bundles;
    std::vector<HookSlot> slots;
    uint32_t next_bundle_id = 1;
    uint64_t approved_capabilities =
        (1ULL << 0) |   // ReadState
        (1ULL << 1) |   // AfterOriginalObserve
        (1ULL << 2) |   // Log
        (1ULL << 3) |   // UiOverlay
        (1ULL << 16) |  // ResourceRedirect
        (1ULL << 17) |  // ReadIl2CppField
        (1ULL << 18) |  // CallIl2CppGetter
        (1ULL << 32) |  // WriteIl2CppField
        (1ULL << 33) |  // CallIl2CppMutator
        (1ULL << 35);   // SkipOriginal
    int dispatcher_required = 0;
    int dispatcher_new = 0;
    int dispatcher_allocated = 0;
    int dispatcher_remaining = 0;
    int dispatcher_blocked = 0;
    std::string last_error;
};

constexpr int kRuleOpOverlayShow = 0;
constexpr int kRuleOpOverlayShowPractice = 1;
constexpr int kRuleOpOverlayHandleStateChange = 2;
constexpr int kRuleOpOverlayHide = 3;
constexpr int kRuleOpOverlayUpdatePlayers = 4;
constexpr int kRuleOpPublishMarginSnapshot = 5;
constexpr int kRuleOpResourceRedirect = 6;
constexpr int kRuleOpOverlayRecordHit = 7;
constexpr int kRuleOpOverlayResetJudgement = 8;
constexpr int kRuleOpOverlayRecordFloorMove = 9;
constexpr int kRuleOpOverlayRecordPlayerHit = 10;
constexpr int kRuleOpOverlayRecordDeath = 11;
constexpr int kRuleOpOverlayRecordHitTiming = 12;
constexpr int kRuleOpResourceApplyEditorRabbit = 13;
constexpr int kRuleOpResourceApplyFloorColor = 14;
constexpr int kRuleOpResourceApplyPlanetColor = 15;
constexpr int kRuleOpResourceSkipPlanetColorOriginal = 16;
constexpr int kRuleOpResourceOverridePlanetColorArg = 17;
constexpr int kRuleOpResourceSkipTileColorOriginal = 18;
constexpr int kRuleOpResourceApplyLogoText = 19;
constexpr int kRuleOpOverlayPollTelemetry = 20;
// Not a native domain effect: the dispatcher only captures the raw instance/argument
// slots and enqueues a per-MOD managed event. The MOD's own postfix callback runs
// later on UnityMain via the managed callback dispatcher. Keep in sync with
// PcCompatRuleOp.ManagedEventCallback in PcCompatFeatureRecipe.cs.
constexpr int kRuleOpManagedEventCallback = 21;
// Observe scrPlayer.HitInputEvent only after the original accepted the action.
// Keep in sync with PcCompatRuleOp.GameplayAcceptedObserve.
constexpr int kRuleOpGameplayAcceptedObserve = 22;
constexpr int kRuleOpManagedSynchronousPrefix = 23;
// A synchronous prefix restricted to MOD-owned host component instances, used to forward a Unity
// render callback to a managed component that has no IL2CPP class-table entry. Behaves exactly like
// kRuleOpManagedSynchronousPrefix except for the instance prefilter below: the hook target
// (UnityEngine.UI.RawImage::OnPopulateMesh) is also used by the game itself, and some of those uses
// rebuild their mesh every frame. Without the filter each of those rebuilds would cross the
// native->managed boundary and allocate an invocation struct just to be told it is not ours.
// Keep in sync with PcCompatRuleOp.ManagedRenderCallback in PcCompatFeatureRecipe.cs.
constexpr int kRuleOpManagedRenderCallback = 24;

// Managed event queue: rule ids emitted by the importer look like
// "managed_event:<patchId>:<callbackTypeFullName>:<callbackMethodName>".
constexpr const char *kManagedEventRuleIdPrefix = "managed_event:";
constexpr const char *kManagedPrefixRuleIdPrefix = "managed_prefix:";
constexpr const char *kManagedRenderRuleIdPrefix = "managed_render:";
constexpr size_t kManagedEventRingCapacity = 2048;
constexpr size_t kManagedEventLifecycleReserve = 64;
constexpr int kManagedEventMaxArgs = 6;
constexpr uint32_t kManagedEventHitMarginCount = 16;

#pragma pack(push, 1)
struct PcCompatManagedEventV2 {
    uint32_t patch_id;
    uint32_t arg_count;
    uint64_t instance_ptr;
    uint64_t args[6];
    uint32_t hit_snapshot_generation;
    uint32_t hit_snapshot_valid;
    uint32_t hit_snapshot_length;
    uint32_t hit_snapshot_attached;
    int32_t hit_snapshot_counts[kManagedEventHitMarginCount];
    uint64_t dispatch_sequence;
    uint64_t invocation_id;
    uint32_t result_kind;
    uint32_t result_valid;
    uint64_t result_value;
    uint32_t run_original;
    uint32_t reserved;
};
#pragma pack(pop)
static_assert(sizeof(PcCompatManagedEventV2) == 184);

// Synchronous Prefix invocation ABI. This frame is mutated in-place by the
// managed callback so primitive/ref/out argument updates reach the original
// dispatcher without a second allocation or a thread hop.
struct PcCompatManagedPrefixInvocationV2 {
    uint32_t struct_size = 0;
    uint32_t abi_version = 0;
    uint32_t argument_count = 0;
    uint32_t result_kind = 0;
    uint64_t instance_ptr = 0;
    uint64_t invocation_id = 0;
    uint32_t run_original = 1;
    uint32_t result_valid = 0;
    uint64_t result_value = 0;
    uint64_t arguments[kManagedEventMaxArgs] = {};
};
static_assert(sizeof(PcCompatManagedPrefixInvocationV2) == 96);
static_assert(offsetof(PcCompatManagedPrefixInvocationV2, arguments) == 48);

struct ManagedQueuedEvent {
    PcCompatManagedEventV2 event{};
    bool lifecycle_boundary = false;
};

struct ManagedEventRing {
    std::mutex lock;
    std::mutex prefix_lifecycle_lock;
    std::condition_variable prefix_lifecycle_condition;
    std::array<ManagedQueuedEvent, kManagedEventRingCapacity> events{};
    ManagedQueuedEvent pending_lifecycle_event{};
    size_t head = 0;
    size_t count = 0;
    uint64_t dropped = 0;
    uint64_t pushed = 0;
    bool has_pending_lifecycle_event = false;
    std::atomic<uint32_t> enabled{0};
    std::atomic<uint32_t> retired{0};
    uint32_t in_flight_prefixes = 0;
    uint64_t registry_epoch = 0;
    std::string mod_id;
};

struct ManagedEventDispatchTarget {
    std::shared_ptr<ManagedEventRing> ring;
    PcModSessionToken pc_mod_session;
    uint32_t managed_event_id = 0;
    bool lifecycle_boundary = false;
};

using ManagedEventDispatchSnapshot = std::vector<ManagedEventDispatchTarget>;

// Snapshots and drain caches retain shared ownership, so per-MOD retirement can
// erase registry entries without invalidating an in-flight hook or drain.
std::mutex g_managed_events_lock;
std::map<uint32_t, std::shared_ptr<ManagedEventRing>> g_managed_event_rings;
std::atomic<uint64_t> g_managed_event_registry_epoch{1};
std::atomic<uint64_t> g_managed_event_registry_generation{1};
std::atomic<uint64_t> g_managed_event_dispatch_sequence{1};
std::atomic<uint64_t> g_managed_prefix_invocation_sequence{1};
struct ManagedPrefixDispatchTarget {
    uint32_t bundle_id = 0;
    uint32_t managed_prefix_id = 0;
    std::shared_ptr<ManagedEventRing> ring;
    PcModSessionToken pc_mod_session;
    // Set for kRuleOpManagedRenderCallback: dispatch only when the instance is a registered
    // MOD-owned host component. See g_managed_render_hosts.
    bool owner_filtered = false;
};
using ManagedPrefixDispatchSnapshot = std::vector<ManagedPrefixDispatchTarget>;
using ManagedPrefixCallback = int (*)(
    uint32_t bundle_id,
    uint32_t prefix_id,
    PcCompatManagedPrefixInvocationV2 *invocation);
std::atomic<ManagedPrefixCallback> g_managed_prefix_callback{nullptr};
thread_local uint32_t g_managed_prefix_callback_depth = 0;

// MOD-owned host component pointers, per MOD, published copy-on-write.
//
// Read on the hook hot path once per candidate call, so the reader takes no lock: it loads the
// shared_ptr and searches the sorted vector it points at. Writers (AddComponent, component destroy,
// session teardown) build a new vector and publish it, which is cheap because the registrations are
// bounded by JipperKeyViewer's own rain pool ceiling of 64.
//
// The set is flat across MODs rather than per-MOD because the hook has only a raw instance pointer
// and no way to know which MOD to ask. Ownership is still recorded so teardown can drop exactly one
// MOD's entries.
struct ManagedRenderHost {
    uint64_t instance_ptr = 0;
    uint32_t bundle_id = 0;
};
std::mutex g_managed_render_hosts_lock;
std::shared_ptr<const std::vector<ManagedRenderHost>> g_managed_render_hosts;
std::map<std::string, std::vector<uint64_t>> g_managed_render_hosts_by_mod;

bool is_managed_render_host(uint64_t instance_ptr) {
    if (instance_ptr == 0)
        return false;
    const auto hosts = std::atomic_load_explicit(
        &g_managed_render_hosts,
        std::memory_order_acquire);
    if (!hosts || hosts->empty())
        return false;
    const auto found = std::lower_bound(
        hosts->begin(),
        hosts->end(),
        instance_ptr,
        [](const ManagedRenderHost &host, uint64_t value) {
            return host.instance_ptr < value;
        });
    return found != hosts->end() && found->instance_ptr == instance_ptr;
}

// Rebuilds and publishes the flat sorted set from the per-MOD lists. Caller holds the lock.
void republish_managed_render_hosts_locked() {
    auto rebuilt = std::make_shared<std::vector<ManagedRenderHost>>();
    for (const auto &entry : g_managed_render_hosts_by_mod) {
        for (const uint64_t instance_ptr : entry.second)
            rebuilt->push_back(ManagedRenderHost{.instance_ptr = instance_ptr, .bundle_id = 0});
    }
    std::sort(
        rebuilt->begin(),
        rebuilt->end(),
        [](const ManagedRenderHost &a, const ManagedRenderHost &b) {
            return a.instance_ptr < b.instance_ptr;
        });
    rebuilt->erase(
        std::unique(
            rebuilt->begin(),
            rebuilt->end(),
            [](const ManagedRenderHost &a, const ManagedRenderHost &b) {
                return a.instance_ptr == b.instance_ptr;
            }),
        rebuilt->end());
    std::atomic_store_explicit(
        &g_managed_render_hosts,
        std::shared_ptr<const std::vector<ManagedRenderHost>>(std::move(rebuilt)),
        std::memory_order_release);
}

struct OwnerOverlayDispatchTarget {
    std::shared_ptr<OwnerOverlaySession> session;
    PcModSessionToken pc_mod_session;
    uint64_t after_op_mask = 0;
    std::vector<uint32_t> bundle_ids;
};
using OwnerOverlayDispatchSnapshot = std::vector<OwnerOverlayDispatchTarget>;

struct SessionRuleMask {
    PcModSessionToken pc_mod_session;
    uint64_t before_op_mask = 0;
    uint64_t after_op_mask = 0;
};
using SessionRuleMaskSnapshot = std::vector<SessionRuleMask>;

struct DispatcherRuntimeSlot {
    std::atomic<void *> original{nullptr};
    std::atomic<uint64_t> before_op_mask{0};
    std::atomic<uint64_t> after_op_mask{0};
    std::atomic<uint32_t> slot_id{0};
    std::atomic<uint32_t> target_kind{0};
    std::atomic<uint32_t> call_count{0};
    std::atomic<uint32_t> fault_count{0};
    std::atomic<uint32_t> enabled{0};
    std::shared_ptr<const ManagedEventDispatchSnapshot> managed_event_after_rules;
    std::shared_ptr<const ManagedPrefixDispatchSnapshot> managed_prefix_before_rules;
    std::shared_ptr<const OwnerOverlayDispatchSnapshot> owner_overlay_after_rules;
    std::shared_ptr<const SessionRuleMaskSnapshot> session_rule_masks;
    bool permanently_bound = false;
    std::string bound_key;
    std::string bound_abi_kind;
    std::string allocated_abi_kind;
    void *bound_function = nullptr;
    void *detour_entry = nullptr;
};

bool pc_mod_session_token_active(const PcModSessionToken &session);

struct DispatcherRuntimePage {
    int base_index = 0;
    int count = 0;
    std::unique_ptr<DispatcherRuntimeSlot[]> slots;
    std::atomic<DispatcherRuntimePage *> next{nullptr};

    DispatcherRuntimePage(int base, int slot_count)
        : base_index(base),
          count(slot_count),
          slots(new (std::nothrow) DispatcherRuntimeSlot[static_cast<size_t>(slot_count)]) {}
};

std::atomic<DispatcherRuntimePage *> g_dispatcher_runtime_head{nullptr};
DispatcherRuntimePage *g_dispatcher_runtime_tail = nullptr;
std::atomic<int> g_dispatcher_capacity{0};

DispatcherRuntimeSlot *dispatcher_runtime_slot(int index) {
    if (index < 0)
        return nullptr;
    auto *page = g_dispatcher_runtime_head.load(std::memory_order_acquire);
    while (page != nullptr) {
        if (index >= page->base_index && index < page->base_index + page->count)
            return &page->slots[static_cast<size_t>(index - page->base_index)];
        page = page->next.load(std::memory_order_acquire);
    }
    return nullptr;
}

template <typename Visitor>
void for_each_dispatcher_runtime_slot(Visitor &&visitor) {
    auto *page = g_dispatcher_runtime_head.load(std::memory_order_acquire);
    while (page != nullptr) {
        for (int offset = 0; offset < page->count; ++offset)
            visitor(page->base_index + offset, page->slots[static_cast<size_t>(offset)]);
        page = page->next.load(std::memory_order_acquire);
    }
}

const std::vector<std::shared_ptr<ManagedEventRing>> &managed_event_rings_for_mod(
    const char *mod_id) {
    struct ThreadCache {
        uint64_t generation = 0;
        std::vector<std::shared_ptr<ManagedEventRing>> rings;
    };
    static thread_local std::map<std::string, ThreadCache> caches;
    auto [cache_entry, inserted] = caches.try_emplace(mod_id);
    auto &cache = cache_entry->second;

    const uint64_t generation =
        g_managed_event_registry_generation.load(std::memory_order_acquire);
    if (!inserted && cache.generation == generation)
        return cache.rings;

    cache.rings.clear();
    {
        std::lock_guard<std::mutex> guard(g_managed_events_lock);
        const uint64_t epoch =
            g_managed_event_registry_epoch.load(std::memory_order_relaxed);
        for (const auto &ring_entry : g_managed_event_rings) {
            const auto &ring = ring_entry.second;
            if (ring != nullptr &&
                ring->registry_epoch == epoch &&
                ring->mod_id == cache_entry->first) {
                cache.rings.push_back(ring);
            }
        }
    }
    cache.generation = generation;
    return cache.rings;
}

bool parse_managed_event_rule_id(const std::string &rule_id, uint32_t *managed_event_id) {
    if (managed_event_id == nullptr)
        return false;
    *managed_event_id = 0;
    const size_t prefix_length = std::strlen(kManagedEventRuleIdPrefix);
    if (rule_id.size() <= prefix_length + 1 ||
        rule_id.compare(0, prefix_length, kManagedEventRuleIdPrefix) != 0)
        return false;

    const char *cursor = rule_id.c_str() + prefix_length;
    char *end = nullptr;
    const unsigned long value = std::strtoul(cursor, &end, 10);
    if (end == cursor || end == nullptr || *end != ':' || value == 0 || value > 0xFFFFFFu)
        return false;

    *managed_event_id = static_cast<uint32_t>(value);
    return true;
}

bool parse_managed_prefix_rule_id(const std::string &rule_id, uint32_t *managed_prefix_id) {
    if (managed_prefix_id == nullptr)
        return false;
    *managed_prefix_id = 0;
    const size_t prefix_length = std::strlen(kManagedPrefixRuleIdPrefix);
    if (rule_id.size() <= prefix_length + 1 ||
        rule_id.compare(0, prefix_length, kManagedPrefixRuleIdPrefix) != 0)
        return false;

    const char *cursor = rule_id.c_str() + prefix_length;
    char *end = nullptr;
    const unsigned long value = std::strtoul(cursor, &end, 10);
    if (end == cursor || end == nullptr || *end != ':' || value == 0 || value > 0xFFFFFFu)
        return false;

    *managed_prefix_id = static_cast<uint32_t>(value);
    return true;
}

// Render-callback rule ids carry their own prefix so a malformed one cannot be silently accepted as
// an ordinary prefix rule, which would drop the owner prefilter and dispatch on every game call.
bool parse_managed_render_rule_id(const std::string &rule_id, uint32_t *managed_prefix_id) {
    if (managed_prefix_id == nullptr)
        return false;
    *managed_prefix_id = 0;
    const size_t prefix_length = std::strlen(kManagedRenderRuleIdPrefix);
    if (rule_id.size() <= prefix_length + 1 ||
        rule_id.compare(0, prefix_length, kManagedRenderRuleIdPrefix) != 0)
        return false;

    const char *cursor = rule_id.c_str() + prefix_length;
    char *end = nullptr;
    const unsigned long value = std::strtoul(cursor, &end, 10);
    if (end == cursor || end == nullptr || *end != ':' || value == 0 || value > 0xFFFFFFu)
        return false;

    *managed_prefix_id = static_cast<uint32_t>(value);
    return true;
}

void push_managed_event(ManagedEventRing *ring,
                        const PcCompatManagedEventV2 &event,
                        bool lifecycle_boundary);

struct FixedOpArgs;
void enqueue_managed_event_rules(int dispatcher_index, const FixedOpArgs &args);

void reset_managed_event_state_locked() {
    std::lock_guard<std::mutex> guard(g_managed_events_lock);
    for (auto &entry : g_managed_event_rings) {
        auto &ring = entry.second;
        ring->retired.store(1, std::memory_order_release);
        std::lock_guard<std::mutex> ring_guard(ring->lock);
        ring->head = 0;
        ring->count = 0;
        ring->dropped = 0;
        ring->pushed = 0;
        ring->pending_lifecycle_event = {};
        ring->has_pending_lifecycle_event = false;
        ring->enabled.store(0, std::memory_order_release);
    }
    for_each_dispatcher_runtime_slot([](int, DispatcherRuntimeSlot &runtime) {
        std::atomic_store_explicit(
            &runtime.managed_event_after_rules,
            std::shared_ptr<const ManagedEventDispatchSnapshot>{},
            std::memory_order_release);
        std::atomic_store_explicit(
            &runtime.managed_prefix_before_rules,
            std::shared_ptr<const ManagedPrefixDispatchSnapshot>{},
            std::memory_order_release);
        std::atomic_store_explicit(
            &runtime.owner_overlay_after_rules,
            std::shared_ptr<const OwnerOverlayDispatchSnapshot>{},
            std::memory_order_release);
    });
    g_managed_event_registry_epoch.fetch_add(1, std::memory_order_acq_rel);
    g_managed_event_registry_generation.fetch_add(1, std::memory_order_acq_rel);
    g_managed_event_dispatch_sequence.store(1, std::memory_order_release);
    g_managed_event_rings.clear();
}

size_t retire_managed_event_rings(const std::vector<uint32_t> &bundle_ids) {
    if (bundle_ids.empty())
        return 0;

    size_t retired_count = 0;
    std::lock_guard<std::mutex> guard(g_managed_events_lock);
    for (const auto bundle_id : bundle_ids) {
        const auto found = g_managed_event_rings.find(bundle_id);
        if (found == g_managed_event_rings.end() || found->second == nullptr)
            continue;
        auto ring = found->second;
        ring->retired.store(1, std::memory_order_release);
        {
            std::unique_lock<std::mutex> prefix_lock(ring->prefix_lifecycle_lock);
            ring->prefix_lifecycle_condition.wait(
                prefix_lock,
                [&ring] { return ring->in_flight_prefixes == 0; });
        }
        {
            std::lock_guard<std::mutex> ring_guard(ring->lock);
            ring->enabled.store(0, std::memory_order_release);
            ring->head = 0;
            ring->count = 0;
            ring->pending_lifecycle_event = {};
            ring->has_pending_lifecycle_event = false;
            ring->registry_epoch = 0;
            ring->mod_id.clear();
        }
        g_managed_event_rings.erase(found);
        ++retired_count;
    }
    if (retired_count != 0)
        g_managed_event_registry_generation.fetch_add(1, std::memory_order_acq_rel);
    return retired_count;
}

constexpr uint64_t kUnityHudStablePointMask =
    (1ULL << kRuleOpOverlayShow) |
    (1ULL << kRuleOpOverlayShowPractice) |
    (1ULL << kRuleOpOverlayHide) |
    (1ULL << kRuleOpOverlayUpdatePlayers) |
    (1ULL << kRuleOpPublishMarginSnapshot) |
    (1ULL << kRuleOpOverlayRecordHit) |
    (1ULL << kRuleOpOverlayResetJudgement) |
    (1ULL << kRuleOpOverlayRecordFloorMove) |
    (1ULL << kRuleOpOverlayRecordPlayerHit) |
    (1ULL << kRuleOpOverlayRecordDeath) |
    (1ULL << kRuleOpOverlayRecordHitTiming) |
    (1ULL << kRuleOpOverlayPollTelemetry);

constexpr uint64_t kOwnerOverlayOpMask =
    (1ULL << kRuleOpOverlayShow) |
    (1ULL << kRuleOpOverlayShowPractice) |
    (1ULL << kRuleOpOverlayHandleStateChange) |
    (1ULL << kRuleOpOverlayHide) |
    (1ULL << kRuleOpOverlayUpdatePlayers) |
    (1ULL << kRuleOpPublishMarginSnapshot) |
    (1ULL << kRuleOpOverlayRecordHit) |
    (1ULL << kRuleOpOverlayResetJudgement) |
    (1ULL << kRuleOpOverlayRecordFloorMove) |
    (1ULL << kRuleOpOverlayRecordPlayerHit) |
    (1ULL << kRuleOpOverlayRecordDeath) |
    (1ULL << kRuleOpOverlayRecordHitTiming) |
    (1ULL << kRuleOpOverlayPollTelemetry);

constexpr uint32_t kTargetKindUnknown = 0;
constexpr uint32_t kTargetKindScnGamePlay = 1;
constexpr uint32_t kTargetKindPressToStartShowText = 2;
constexpr uint32_t kTargetKindStateBehaviourChangeState = 3;
constexpr uint32_t kTargetKindUiControllerWipeToBlack = 4;
constexpr uint32_t kTargetKindEditorResetScene = 5;
constexpr uint32_t kTargetKindControllerStartLoadingScene = 6;
constexpr uint32_t kTargetKindMistakesSetPlayerCount = 7;
constexpr uint32_t kTargetKindMarginTrackerAddHit = 8;
constexpr uint32_t kTargetKindMarginTrackerReset = 9;
constexpr uint32_t kTargetKindPlanetMoveToNextFloor = 10;
constexpr uint32_t kTargetKindPlayerHit = 11;
constexpr uint32_t kTargetKindPlayerDie = 12;
constexpr uint32_t kTargetKindMiscGetHitMargin = 13;
constexpr uint32_t kTargetKindMarginTrackerCalculatePercentAcc = 14;
constexpr uint32_t kTargetKindControllerQuitToMainMenu = 15;
constexpr uint32_t kTargetKindScnEditorOttoUpdate = 16;
constexpr uint32_t kTargetKindScrFloorStart = 17;
constexpr uint32_t kTargetKindScrPlanetStart = 18;
constexpr uint32_t kTargetKindScrLogoTextAwake = 19;
constexpr uint32_t kTargetKindPlanetarySystemRainbowMode = 20;
constexpr uint32_t kTargetKindPlanetarySystemEnbyMode = 21;
constexpr uint32_t kTargetKindScrLogoTextUpdateColors = 22;
constexpr uint32_t kTargetKindEditorSwitchToEditMode = 31;

bool is_managed_event_lifecycle_boundary(uint32_t target_kind) {
    return target_kind == kTargetKindScnGamePlay ||
           target_kind == kTargetKindPressToStartShowText ||
           target_kind == kTargetKindUiControllerWipeToBlack ||
           target_kind == kTargetKindEditorResetScene ||
           target_kind == kTargetKindEditorSwitchToEditMode ||
           target_kind == kTargetKindControllerStartLoadingScene;
}
constexpr uint32_t kTargetKindScrLogoTextLateUpdate = 23;
constexpr uint32_t kTargetKindScrFloorSetTileColor = 24;
constexpr uint32_t kTargetKindPlanetRendererLoadPlanetColor = 25;
constexpr uint32_t kTargetKindPlanetRendererSetRainbow = 26;
constexpr uint32_t kTargetKindPlanetRendererSetColor = 27;
constexpr uint32_t kTargetKindPlanetRendererSetColorArg = 28;
constexpr uint32_t kTargetKindControllerPlayerControlUpdate = 29;
constexpr uint32_t kTargetKindPlayerHitInputEvent = 30;

struct ColorValue {
    float r = 0.0f;
    float g = 0.0f;
    float b = 0.0f;
    float a = 1.0f;
};

struct Vector2Value {
    float x = 0.0f;
    float y = 0.0f;
};

struct NullableColorValue {
    uint8_t has_value = 0;
    uint8_t padding[3]{};
    ColorValue value{};
};

static_assert(sizeof(NullableColorValue) == 20);

// IL2CPP methodPointer signatures always carry an implicit trailing
// `const MethodInfo *method` parameter (the game's caller places it in the
// next GP register after the declared parameters). Detours must declare and
// forward it: calling the continuation without it hands the original method
// a garbage MethodInfo*, which crashes as soon as the callee consumes it
// (generic sharing, ldtoken, interface dispatch, runtime checks).
using InstanceVoid0Fn = void (*)(void *, void *);
using InstanceVoid1Fn = void (*)(void *, void *, void *);
using InstanceVoidInt1Fn = void (*)(void *, int, void *);
using InstanceVoidPtrFloatIntFn = void (*)(void *, void *, float, int, void *);
using InstanceVoid3Fn = void (*)(void *, void *, void *, void *, void *);
using InstanceVoidBoolBoolPtrBoolFn = void (*)(void *, bool, bool, void *, bool, void *);
using InstanceVoidColor1Fn = void (*)(void *, ColorValue, void *);
using InstanceVoidIntBoolFn = void (*)(void *, int, bool, void *);
using InstanceVoidPtrBoolFn = void (*)(void *, void *, bool, void *);
using InstanceBool1Fn = bool (*)(void *, bool, void *);
using InstanceBool2Fn = bool (*)(void *, int, bool, void *);
using InstanceBoolBoolIntFn = bool (*)(void *, bool, int, void *);
using StaticVoid1Fn = void (*)(int, void *);
using StaticIntFloatFloatBoolFloatFloatDoubleFn = int (*)(float, float, bool, float, float, double, void *);

std::mutex g_lock;
// Binary recipe registration and global clearing must be serialized.  The
// lifecycle registry intentionally keeps stable program indices for the
// lifetime of the process, so a load cannot race a clear between registration
// and insertion into g_state.
std::mutex g_lifecycle_operation_lock;
RuleState g_state;
std::map<std::string, std::map<uint32_t, ManagedPrefixOrderMetadata>>
    g_managed_prefix_order_plans;
std::map<std::string, std::map<uint32_t, ManagedPrefixOrderMetadata>>
    g_managed_prefix_order_plan_staging;
std::map<std::string, std::map<uint32_t, ManagedPrefixOrderMetadata>>
    g_managed_postfix_order_plans;
std::map<std::string, std::map<uint32_t, ManagedPrefixOrderMetadata>>
    g_managed_postfix_order_plan_staging;
// This session is not an owner fallback. It holds official game facts sampled
// once per native observation point. Owner sessions retain their own HUD
// lifecycle, counters and presentation state; consumers such as JPOV read this
// state directly instead of borrowing another MOD's projection.
OwnerOverlaySession g_legacy_overlay_session;
std::mutex g_owner_overlay_sessions_lock;
std::map<std::string, std::shared_ptr<OwnerOverlaySession>> g_owner_overlay_sessions;
std::atomic<uint64_t> g_owner_overlay_registry_generation{1};
std::atomic<uint64_t> g_next_owner_overlay_session_generation{1};
thread_local OwnerOverlaySession *g_active_owner_overlay_session = nullptr;

OwnerOverlaySession &active_owner_overlay_session() {
    return g_active_owner_overlay_session != nullptr
        ? *g_active_owner_overlay_session
        : g_legacy_overlay_session;
}

OverlayRuntimeState &active_overlay_state() {
    return active_owner_overlay_session().state;
}

class OwnerOverlayScope final {
public:
    explicit OwnerOverlayScope(OwnerOverlaySession *session)
        : previous_(g_active_owner_overlay_session) {
        g_active_owner_overlay_session = session;
    }

    OwnerOverlayScope(const OwnerOverlayScope &) = delete;
    OwnerOverlayScope &operator=(const OwnerOverlayScope &) = delete;

    ~OwnerOverlayScope() {
        g_active_owner_overlay_session = previous_;
    }

private:
    OwnerOverlaySession *previous_ = nullptr;
};

std::shared_ptr<OwnerOverlaySession> get_or_create_owner_overlay_session(
    const std::string &mod_id) {
    if (mod_id.empty())
        return {};
    std::lock_guard<std::mutex> guard(g_owner_overlay_sessions_lock);
    const auto found = g_owner_overlay_sessions.find(mod_id);
    if (found != g_owner_overlay_sessions.end() &&
        found->second != nullptr &&
        found->second->retired.load(std::memory_order_acquire) == 0) {
        return found->second;
    }

    auto session = std::make_shared<OwnerOverlaySession>();
    session->mod_id = mod_id;
    session->session_generation =
        g_next_owner_overlay_session_generation.fetch_add(1, std::memory_order_relaxed);
    uint32_t generation_seed = static_cast<uint32_t>(
        session->session_generation ^ (session->session_generation >> 32u));
    if (generation_seed == 0)
        generation_seed = 1;
    session->state.generation.store(generation_seed, std::memory_order_relaxed);
    g_owner_overlay_sessions[mod_id] = session;
    g_owner_overlay_registry_generation.fetch_add(1, std::memory_order_release);
    return session;
}

std::shared_ptr<OwnerOverlaySession> owner_overlay_session_for_mod(
    const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return {};
    struct ThreadCache {
        uint64_t registry_generation = 0;
        std::shared_ptr<OwnerOverlaySession> session;
    };
    static thread_local std::map<std::string, ThreadCache> caches;
    auto [entry, inserted] = caches.try_emplace(mod_id);
    auto &cache = entry->second;
    const uint64_t generation =
        g_owner_overlay_registry_generation.load(std::memory_order_acquire);
    if (!inserted && cache.registry_generation == generation)
        return cache.session;

    {
        std::lock_guard<std::mutex> guard(g_owner_overlay_sessions_lock);
        const auto found = g_owner_overlay_sessions.find(entry->first);
        cache.session = found != g_owner_overlay_sessions.end()
            ? found->second
            : std::shared_ptr<OwnerOverlaySession>{};
    }
    cache.registry_generation = generation;
    return cache.session;
}

std::shared_ptr<OwnerOverlaySession> default_owner_overlay_session() {
    std::lock_guard<std::mutex> guard(g_owner_overlay_sessions_lock);
    std::shared_ptr<OwnerOverlaySession> fallback;
    for (const auto &entry : g_owner_overlay_sessions) {
        const auto &session = entry.second;
        if (session == nullptr ||
            session->retired.load(std::memory_order_acquire) != 0) {
            continue;
        }
        if (fallback == nullptr)
            fallback = session;
        if (session->state.visible.load(std::memory_order_acquire) != 0)
            return session;
    }
    return fallback;
}

OverlayRuntimeState &default_overlay_state_for_legacy_api() {
    // Hold the selected owner for the duration of the exported scalar read,
    // even if another thread retires it immediately after this lookup.
    static thread_local std::shared_ptr<OwnerOverlaySession> selected;
    selected = default_owner_overlay_session();
    return selected != nullptr ? selected->state : g_legacy_overlay_session.state;
}

bool any_owner_overlay_visible() {
    std::lock_guard<std::mutex> guard(g_owner_overlay_sessions_lock);
    return std::any_of(
        g_owner_overlay_sessions.begin(),
        g_owner_overlay_sessions.end(),
        [](const auto &entry) {
            const auto &session = entry.second;
            return session != nullptr &&
                session->retired.load(std::memory_order_acquire) == 0 &&
                session->state.visible.load(std::memory_order_acquire) != 0;
        });
}

void retire_owner_overlay_session(const std::string &mod_id) {
    if (mod_id.empty())
        return;
    std::shared_ptr<OwnerOverlaySession> retired;
    {
        std::lock_guard<std::mutex> guard(g_owner_overlay_sessions_lock);
        const auto found = g_owner_overlay_sessions.find(mod_id);
        if (found == g_owner_overlay_sessions.end())
            return;
        retired = found->second;
        g_owner_overlay_sessions.erase(found);
        g_owner_overlay_registry_generation.fetch_add(1, std::memory_order_release);
    }
    if (retired != nullptr) {
        retired->retired.store(1, std::memory_order_release);
        retired->state.visible.store(0, std::memory_order_release);
        retired->state.generation.fetch_add(1, std::memory_order_release);
    }
}

void clear_owner_overlay_sessions() {
    std::vector<std::shared_ptr<OwnerOverlaySession>> retired;
    {
        std::lock_guard<std::mutex> guard(g_owner_overlay_sessions_lock);
        retired.reserve(g_owner_overlay_sessions.size());
        for (auto &entry : g_owner_overlay_sessions)
            retired.push_back(std::move(entry.second));
        g_owner_overlay_sessions.clear();
        g_owner_overlay_registry_generation.fetch_add(1, std::memory_order_release);
    }
    for (const auto &session : retired) {
        if (session == nullptr)
            continue;
        session->retired.store(1, std::memory_order_release);
        session->state.visible.store(0, std::memory_order_release);
        session->state.generation.fetch_add(1, std::memory_order_release);
    }
}
using OverlayChangedCallback = void (*)(uint32_t generation);
std::atomic<OverlayChangedCallback> g_overlay_changed_callback{nullptr};
constexpr uint32_t kOverlayChangedCallbackDescriptorSlot = 0x3000F001u;
constexpr uint32_t kManagedPrefixCallbackDescriptorSlot = 0x3000F002u;
constexpr int64_t kMarginSnapshotCallbackIntervalMs = 50;
std::mutex g_metadata_resolve_lock;
Il2CppMetadataApi g_il2cpp_metadata;
std::atomic<int32_t> g_margin_percent_acc_offset{-1};
std::atomic<int32_t> g_margin_percent_x_acc_offset{-1};
std::mutex g_hit_margin_metadata_lock;
std::atomic<int32_t> g_margin_hit_counts_offset{-1};
std::atomic<uint32_t> g_next_api_descriptor_slot{0x20000000u};
std::atomic<uint32_t> g_next_scalar_descriptor_slot{0x40000000u};
std::atomic<void *> g_mistakes_margin_trackers_field{nullptr};
std::atomic<uintptr_t> g_margin_tracker_instance{0};

constexpr uint32_t kHitMarginSnapshotAbiVersion = 1;
constexpr uint32_t kHitMarginSnapshotMaxCounts = 16;
constexpr int64_t kHitMarginFallbackPollIntervalMs = 100;
static_assert(kHitMarginSnapshotMaxCounts == kManagedEventHitMarginCount);

#pragma pack(push, 1)
struct PcCompatHitMarginSnapshotV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t generation;
    uint32_t valid;
    uint32_t length;
    uint32_t checksum;
    uint64_t tracker;
    int32_t counts[kHitMarginSnapshotMaxCounts];
};
#pragma pack(pop)

static_assert(sizeof(PcCompatHitMarginSnapshotV1) == 96);

struct HitMarginSnapshotState {
    std::mutex publish_lock;
    std::atomic<uint32_t> sequence{0};
    std::atomic<uint32_t> generation{0};
    std::atomic<uint32_t> valid{0};
    std::atomic<uint32_t> length{0};
    std::atomic<uint32_t> checksum{0};
    std::atomic<uintptr_t> tracker{0};
    std::array<std::atomic<int32_t>, kHitMarginSnapshotMaxCounts> counts{};
};

HitMarginSnapshotState g_hit_margin_snapshot;
std::atomic<int64_t> g_last_hit_margin_fallback_poll_ms{0};
std::atomic<int64_t> g_last_hit_margin_authoritative_publish_ms{0};
std::atomic<uint32_t> g_resource_change_rabbit{0};
std::atomic<uint32_t> g_resource_change_ball_color{0};
std::atomic<uint32_t> g_resource_change_tile_color{0};
constexpr ColorValue kResourceDefaultPlanetColor{0.8125f, 0.70703125f, 0.96875f, 1.0f};
constexpr ColorValue kResourceDefaultTitleColor{0.56640625f, 0.46875f, 0.6328125f, 1.0f};
constexpr ColorValue kResourceDefaultTileColor{0.94921875f, 0.87109375f, 1.0f, 1.0f};
std::atomic<float> g_resource_planet_r{kResourceDefaultPlanetColor.r};
std::atomic<float> g_resource_planet_g{kResourceDefaultPlanetColor.g};
std::atomic<float> g_resource_planet_b{kResourceDefaultPlanetColor.b};
std::atomic<float> g_resource_planet_a{kResourceDefaultPlanetColor.a};
std::atomic<float> g_resource_title_r{kResourceDefaultTitleColor.r};
std::atomic<float> g_resource_title_g{kResourceDefaultTitleColor.g};
std::atomic<float> g_resource_title_b{kResourceDefaultTitleColor.b};
std::atomic<float> g_resource_title_a{kResourceDefaultTitleColor.a};
std::atomic<float> g_resource_tile_r{kResourceDefaultTileColor.r};
std::atomic<float> g_resource_tile_g{kResourceDefaultTileColor.g};
std::atomic<float> g_resource_tile_b{kResourceDefaultTileColor.b};
std::atomic<float> g_resource_tile_a{kResourceDefaultTileColor.a};
std::mutex g_resource_state_lock;
std::string g_resource_state_mod_id;
int64_t g_resource_state_generation = 0;
std::string g_resource_pack_name{"Jipper Resource Pack"};
constexpr uint32_t kResourceContributionRabbit = 1u << 0;
constexpr uint32_t kResourceContributionPlanet = 1u << 1;
constexpr uint32_t kResourceContributionTile = 1u << 2;

struct ResourceOwnerKey {
    std::string mod_id;
    int64_t session_generation = 0;

    bool operator<(const ResourceOwnerKey &other) const {
        if (mod_id != other.mod_id)
            return mod_id < other.mod_id;
        return session_generation < other.session_generation;
    }
};

bool resource_owner_key_equal(
    const ResourceOwnerKey &left,
    const ResourceOwnerKey &right) {
    return left.mod_id == right.mod_id &&
           left.session_generation == right.session_generation;
}

struct ResourceContribution {
    uint32_t feature_mask = 0;
    ColorValue planet_color = kResourceDefaultPlanetColor;
    ColorValue title_color = kResourceDefaultTitleColor;
    ColorValue tile_color = kResourceDefaultTileColor;
    std::string resource_pack_name;
    uint64_t registration_sequence = 0;
};

struct ResourceEffectiveState {
    bool present = false;
    ResourceOwnerKey owner;
    ResourceContribution contribution;
};

std::map<ResourceOwnerKey, ResourceContribution> g_resource_contributions;
std::map<std::string, int64_t> g_resource_latest_generation_by_mod;
uint64_t g_resource_contribution_sequence = 0;
ResourceEffectiveState g_resource_effective_state;
constexpr uint32_t kResourceRestoreRabbit = 1u << 0;
constexpr uint32_t kResourceRestorePlanet = 1u << 1;
constexpr uint32_t kResourceRestoreTile = 1u << 2;
std::atomic<uint32_t> g_resource_pending_restore_mask{0};
std::mutex g_resource_tracked_lock;
void *g_resource_editor_handle = nullptr;
void *g_resource_original_rabbit_sprite_handle = nullptr;
std::vector<void *> g_resource_planet_handles;
std::vector<void *> g_resource_floor_handles;
std::set<void *> g_resource_planet_objects;
std::set<void *> g_resource_floor_objects;
void *g_resource_logo_text_handle = nullptr;
std::mutex g_resource_asset_lock;
std::string g_resource_rabbit_sprite_mod_id;
int64_t g_resource_rabbit_sprite_generation = 0;
void *g_resource_rabbit_sprite_handle = nullptr;
struct ResourceSpriteContribution {
    void *handle = nullptr;
    uint64_t registration_sequence = 0;
};
std::map<ResourceOwnerKey, ResourceSpriteContribution> g_resource_sprite_contributions;
std::map<std::string, int64_t> g_resource_sprite_latest_generation_by_mod;
uint64_t g_resource_sprite_sequence = 0;
std::mutex g_timeline_state_lock;
std::vector<int32_t> g_checkpoint_seq_ids;
std::string g_level_identity;
int32_t g_session_start_seq_id = 0;
bool g_session_start_seq_valid = false;
bool g_floor_metadata_initialized = false;
bool g_music_has_played = false;
std::atomic<uint32_t> g_shared_overlay_session_visible{0};
std::mutex g_hook_coordinator_lock;
std::condition_variable g_hook_coordinator_condition;
std::once_flag g_hook_coordinator_once;
uint64_t g_hook_work_generation = 0;
std::atomic<uint32_t> g_presentation_install_requested{0};

uint32_t float_to_bits(float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

float bits_to_float(uint32_t bits) {
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

uint64_t double_to_bits(double value) {
    uint64_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

double bits_to_double(uint64_t bits) {
    double value = 0.0;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

template <typename T>
bool atomic_store_if_changed(std::atomic<T> &target, T value) {
    const T previous = target.exchange(value, std::memory_order_acq_rel);
    return previous != value;
}

template <typename T>
bool read_instance_value(void *instance, int32_t offset, T &value) {
    if (instance == nullptr || offset <= 0)
        return false;
    std::memcpy(&value, static_cast<const char *>(instance) + offset, sizeof(value));
    return true;
}

int64_t steady_time_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

std::string read_file(const char *path) {
    // The binary recipe path caps files at 16 MiB; apply the same ceiling to
    // the JSON audit path so a corrupt or hostile cache cannot exhaust memory.
    constexpr std::streamoff kMaxJsonBytes = 16 * 1024 * 1024;
    std::ifstream input(path, std::ios::in | std::ios::binary | std::ios::ate);
    if (!input)
        return {};
    const auto size = input.tellg();
    if (size < 0 || size > kMaxJsonBytes)
        return {};
    input.seekg(0, std::ios::beg);

    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

void skip_ws(const std::string &text, size_t &pos) {
    while (pos < text.size() && std::isspace(static_cast<unsigned char>(text[pos])))
        ++pos;
}

bool is_string_escaped(const std::string &text, size_t quote_pos) {
    size_t slashes = 0;
    while (quote_pos > slashes && text[quote_pos - slashes - 1] == '\\')
        ++slashes;
    return (slashes % 2) != 0;
}

size_t find_matching(const std::string &text, size_t open_pos, char open_ch, char close_ch) {
    bool in_string = false;
    int depth = 0;
    for (size_t pos = open_pos; pos < text.size(); ++pos) {
        const char ch = text[pos];
        if (ch == '"' && !is_string_escaped(text, pos)) {
            in_string = !in_string;
            continue;
        }

        if (in_string)
            continue;

        if (ch == open_ch) {
            ++depth;
        } else if (ch == close_ch) {
            --depth;
            if (depth == 0)
                return pos;
        }
    }

    return std::string::npos;
}

bool read_json_string_at(const std::string &text, size_t &pos, std::string &out) {
    skip_ws(text, pos);
    if (pos >= text.size() || text[pos] != '"')
        return false;

    out.clear();
    for (++pos; pos < text.size(); ++pos) {
        const char ch = text[pos];
        if (ch == '\\') {
            if (pos + 1 >= text.size())
                return false;
            const char esc = text[++pos];
            switch (esc) {
                case '"':
                case '\\':
                case '/':
                    out.push_back(esc);
                    break;
                case 'b':
                    out.push_back('\b');
                    break;
                case 'f':
                    out.push_back('\f');
                    break;
                case 'n':
                    out.push_back('\n');
                    break;
                case 'r':
                    out.push_back('\r');
                    break;
                case 't':
                    out.push_back('\t');
                    break;
                default:
                    // We only emit ASCII schema strings today. Preserve unknown
                    // escapes verbatim instead of failing the whole bundle.
                    out.push_back(esc);
                    break;
            }
            continue;
        }

        if (ch == '"') {
            ++pos;
            return true;
        }

        out.push_back(ch);
    }

    return false;
}

size_t find_property_value(const std::string &text, const char *property) {
    const std::string key = std::string("\"") + property + "\"";
    auto pos = text.find(key);
    if (pos == std::string::npos)
        return std::string::npos;

    pos = text.find(':', pos + key.size());
    if (pos == std::string::npos)
        return std::string::npos;

    ++pos;
    skip_ws(text, pos);
    return pos;
}

bool read_string_property(const std::string &object, const char *property, std::string &out, bool required, std::string &error) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        if (required)
            error = std::string("missing string property ") + property;
        return !required;
    }

    if (!read_json_string_at(object, pos, out)) {
        error = std::string("invalid string property ") + property;
        return false;
    }

    if (required && out.empty()) {
        error = std::string("empty string property ") + property;
        return false;
    }

    return true;
}

bool read_required_string_property_allow_empty(const std::string &object,
                                               const char *property,
                                               std::string &out,
                                               std::string &error) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        error = std::string("missing string property ") + property;
        return false;
    }
    if (!read_json_string_at(object, pos, out)) {
        error = std::string("invalid string property ") + property;
        return false;
    }
    return true;
}

bool read_int_at(const std::string &text, size_t &pos, int &out) {
    skip_ws(text, pos);
    if (pos >= text.size())
        return false;

    char *end = nullptr;
    const long value = std::strtol(text.c_str() + pos, &end, 10);
    if (end == text.c_str() + pos)
        return false;

    out = static_cast<int>(value);
    pos = static_cast<size_t>(end - text.c_str());
    return true;
}

bool read_u64_at(const std::string &text, size_t &pos, uint64_t &out) {
    skip_ws(text, pos);
    if (pos >= text.size())
        return false;

    char *end = nullptr;
    const unsigned long long value = std::strtoull(text.c_str() + pos, &end, 10);
    if (end == text.c_str() + pos)
        return false;

    out = static_cast<uint64_t>(value);
    pos = static_cast<size_t>(end - text.c_str());
    return true;
}

bool read_int_property(const std::string &object, const char *property, int &out, bool required, std::string &error) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        if (required)
            error = std::string("missing int property ") + property;
        return !required;
    }

    if (!read_int_at(object, pos, out)) {
        error = std::string("invalid int property ") + property;
        return false;
    }

    return true;
}

bool read_optional_int_property(const std::string &object, const char *property, bool &has_value, int &out, std::string &error) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        has_value = false;
        return true;
    }

    if (object.compare(pos, 4, "null") == 0) {
        has_value = false;
        return true;
    }

    has_value = true;
    if (!read_int_at(object, pos, out)) {
        error = std::string("invalid optional int property ") + property;
        return false;
    }

    return true;
}

bool read_u64_property(const std::string &object, const char *property, uint64_t &out, bool required, std::string &error) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        if (required)
            error = std::string("missing u64 property ") + property;
        return !required;
    }

    if (!read_u64_at(object, pos, out)) {
        error = std::string("invalid u64 property ") + property;
        return false;
    }

    return true;
}

bool read_bool_property(const std::string &object, const char *property, bool &out, bool default_value) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        out = default_value;
        return true;
    }

    if (object.compare(pos, 4, "true") == 0) {
        out = true;
        return true;
    }
    if (object.compare(pos, 5, "false") == 0) {
        out = false;
        return true;
    }

    return false;
}

bool read_required_bool_property(const std::string &object,
                                 const char *property,
                                 bool &out,
                                 std::string &error) {
    const auto pos = find_property_value(object, property);
    if (pos == std::string::npos) {
        error = std::string("missing bool property ") + property;
        return false;
    }
    if (object.compare(pos, 4, "true") == 0) {
        out = true;
        return true;
    }
    if (object.compare(pos, 5, "false") == 0) {
        out = false;
        return true;
    }
    error = std::string("invalid bool property ") + property;
    return false;
}

bool read_array_property(const std::string &object, const char *property, std::string &array_text, std::string &error) {
    auto pos = find_property_value(object, property);
    if (pos == std::string::npos || pos >= object.size() || object[pos] != '[') {
        error = std::string("missing array property ") + property;
        return false;
    }

    const auto close = find_matching(object, pos, '[', ']');
    if (close == std::string::npos) {
        error = std::string("unterminated array property ") + property;
        return false;
    }

    array_text = object.substr(pos, close - pos + 1);
    return true;
}

bool extract_array_objects(const std::string &array_text, std::vector<std::string> &objects, std::string &error) {
    objects.clear();
    size_t pos = 0;
    skip_ws(array_text, pos);
    if (pos >= array_text.size() || array_text[pos] != '[') {
        error = "array text does not start with '['";
        return false;
    }

    ++pos;
    while (pos < array_text.size()) {
        skip_ws(array_text, pos);
        if (pos < array_text.size() && array_text[pos] == ']')
            return true;

        if (pos >= array_text.size() || array_text[pos] != '{') {
            error = "array item is not an object";
            return false;
        }

        const auto close = find_matching(array_text, pos, '{', '}');
        if (close == std::string::npos) {
            error = "unterminated object in array";
            return false;
        }

        objects.push_back(array_text.substr(pos, close - pos + 1));
        pos = close + 1;
        skip_ws(array_text, pos);

        if (pos < array_text.size() && array_text[pos] == ',') {
            ++pos;
            continue;
        }
        if (pos < array_text.size() && array_text[pos] == ']')
            return true;
    }

    error = "unterminated array";
    return false;
}

bool extract_array_strings(const std::string &array_text,
                           std::vector<std::string> &values,
                           std::string &error) {
    values.clear();
    size_t pos = 0;
    skip_ws(array_text, pos);
    if (pos >= array_text.size() || array_text[pos] != '[') {
        error = "array text does not start with '['";
        return false;
    }

    ++pos;
    for (;;) {
        skip_ws(array_text, pos);
        if (pos >= array_text.size()) {
            error = "unterminated string array";
            return false;
        }
        if (array_text[pos] == ']')
            return true;

        std::string value;
        if (!read_json_string_at(array_text, pos, value)) {
            error = "string array contains a non-string item";
            return false;
        }
        if (value.empty()) {
            error = "string array contains an empty item";
            return false;
        }
        values.push_back(std::move(value));

        skip_ws(array_text, pos);
        if (pos >= array_text.size()) {
            error = "unterminated string array";
            return false;
        }
        if (array_text[pos] == ']')
            return true;
        if (array_text[pos] != ',') {
            error = "string array items are not comma-separated";
            return false;
        }
        ++pos;
    }
}

bool read_string_array_property(const std::string &object,
                                const char *property,
                                std::vector<std::string> &values,
                                std::string &error) {
    std::string array_text;
    return read_array_property(object, property, array_text, error) &&
           extract_array_strings(array_text, values, error);
}

bool parse_rule(const std::string &object, RuntimeRule &rule, std::string &error) {
    if (!read_string_property(object, "id", rule.id, true, error))
        return false;
    if (!read_string_property(object, "featureId", rule.feature_id, true, error))
        return false;
    if (!read_string_property(object, "stage", rule.stage, true, error))
        return false;
    if (!read_int_property(object, "stageCode", rule.stage_code, true, error))
        return false;
    if (!read_string_property(object, "op", rule.op, true, error))
        return false;
    if (!read_int_property(object, "opCode", rule.op_code, true, error))
        return false;
    if (!read_u64_property(object, "requiredCapabilities", rule.required_capabilities, true, error))
        return false;
    if (!read_bool_property(object, "defaultEnabled", rule.default_enabled, true)) {
        error = "invalid bool property defaultEnabled";
        return false;
    }
    if (!read_string_property(object, "source", rule.source, false, error))
        return false;

    if (rule.stage_code < 0 || rule.op_code < 0) {
        error = "negative stage/op code is not allowed";
        return false;
    }

    return true;
}

bool parse_target(const std::string &object, RuntimeTarget &target, std::string &error) {
    if (!read_int_property(object, "id", target.id, true, error))
        return false;
    if (!read_string_property(object, "assemblyName", target.assembly_name, true, error))
        return false;
    if (!read_required_string_property_allow_empty(object, "namespace", target.namespace_name, error))
        return false;
    if (!read_string_property(object, "typeName", target.type_name, true, error))
        return false;
    if (!read_string_property(object, "methodName", target.method_name, true, error))
        return false;
    if (!read_required_bool_property(object, "isStatic", target.is_static, error))
        return false;
    if (!read_int_property(object, "genericArity", target.generic_arity, true, error))
        return false;
    if (!read_string_property(object, "returnType", target.return_type, true, error))
        return false;
    if (!read_string_array_property(object, "parameterTypes", target.parameter_types, error))
        return false;
    if (!read_optional_int_property(object, "paramCount", target.has_param_count, target.param_count, error))
        return false;
    if (!read_string_property(object, "abiKind", target.abi_kind, false, error))
        return false;
    if (target.abi_kind.empty())
        target.abi_kind = "Unknown";

    std::string rules_array;
    if (!read_array_property(object, "rules", rules_array, error))
        return false;

    std::vector<std::string> rule_objects;
    if (!extract_array_objects(rules_array, rule_objects, error))
        return false;

    for (const auto &rule_object : rule_objects) {
        RuntimeRule rule;
        if (!parse_rule(rule_object, rule, error))
            return false;
        target.rules.push_back(std::move(rule));
    }

    if (target.id <= 0) {
        error = "target id must be positive";
        return false;
    }
    if (target.generic_arity < 0) {
        error = "target genericArity must not be negative";
        return false;
    }
    if (target.has_param_count && target.param_count != static_cast<int>(target.parameter_types.size())) {
        error = "target paramCount does not match parameterTypes";
        return false;
    }
    target.has_param_count = true;
    target.param_count = static_cast<int>(target.parameter_types.size());
    if (target.rules.empty()) {
        error = "target has no rules: " + target.type_name + "." + target.method_name;
        return false;
    }

    return true;
}

bool parse_bundle(const std::string &json, const char *path, RuntimeBundle &bundle, std::string &error) {
    if (json.empty()) {
        error = "empty or unreadable file";
        return false;
    }

    std::string format;
    if (!read_string_property(json, "formatVersion", format, true, error))
        return false;
    if (format != "mvp-fixed-op-v2") {
        error = "unsupported formatVersion=" + format;
        return false;
    }

    bundle.path = path;
    if (!read_string_property(json, "modId", bundle.mod_id, true, error))
        return false;
    if (!read_string_property(json, "recipeId", bundle.recipe_id, true, error))
        return false;
    if (!read_string_property(json, "compatibility", bundle.compatibility, true, error))
        return false;
    if (!read_u64_property(json, "requiredCapabilities", bundle.required_capabilities, true, error))
        return false;

    std::string targets_array;
    if (!read_array_property(json, "targets", targets_array, error))
        return false;

    std::vector<std::string> target_objects;
    if (!extract_array_objects(targets_array, target_objects, error))
        return false;

    for (const auto &target_object : target_objects) {
        RuntimeTarget target;
        if (!parse_target(target_object, target, error))
            return false;
        bundle.targets.push_back(std::move(target));
    }

    if (bundle.targets.empty()) {
        error = "bundle has no targets";
        return false;
    }

    return true;
}

int count_targets(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles)
        count += bundle.targets.size();
    return static_cast<int>(count);
}

int count_rules(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles) {
        for (const auto &target : bundle.targets)
            count += target.rules.size();
    }
    return static_cast<int>(count);
}

int count_ui_lifecycle_programs(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles)
        count += bundle.ui_lifecycle_programs.size();
    return static_cast<int>(count);
}

int count_ui_object_nodes(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles)
        count += bundle.ui_objects.size();
    return static_cast<int>(count);
}

int count_ui_component_ops(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles) {
        for (const auto &node : bundle.ui_objects)
            count += node.initialization.size();
    }
    return static_cast<int>(count);
}

int count_ui_resource_bindings(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles)
        count += bundle.ui_resources.size();
    return static_cast<int>(count);
}

int count_ui_bytecode_instructions(const RuleState &state) {
    size_t count = 0;
    for (const auto &bundle : state.bundles)
        count += bundle.ui_bytecode.size();
    return static_cast<int>(count);
}

int count_rules_for_target_locked(const char *type_name, const char *method_name, int param_count) {
    if (type_name == nullptr || method_name == nullptr)
        return 0;

    int count = 0;
    for (const auto &bundle : g_state.bundles) {
        for (const auto &target : bundle.targets) {
            if (target.type_name != type_name || target.method_name != method_name)
                continue;
            if (param_count >= 0) {
                if (!target.has_param_count || target.param_count != param_count)
                    continue;
            }
            count += static_cast<int>(target.rules.size());
        }
    }
    return count;
}

std::string normalize_assembly_name(std::string name);
std::string canonical_identity_type_name(std::string name);

std::string full_declaring_type_name(const std::string &namespace_name,
                                     const std::string &type_name) {
    if (namespace_name.empty())
        return type_name;
    const std::string prefix = namespace_name + ".";
    return type_name.starts_with(prefix) ? type_name : prefix + type_name;
}

std::string target_key(const RuntimeTarget &target) {
    std::ostringstream key;
    key << normalize_assembly_name(target.assembly_name) << "|"
        << full_declaring_type_name(target.namespace_name, target.type_name) << "|"
        << target.method_name << "|"
        << (target.is_static ? "static" : "instance") << "|"
        << target.generic_arity << "|"
        << canonical_identity_type_name(target.return_type) << "|";
    for (size_t index = 0; index < target.parameter_types.size(); ++index) {
        if (index != 0)
            key << ";";
        key << canonical_identity_type_name(target.parameter_types[index]);
    }
    return key.str();
}

bool is_first_dispatcher_abi_supported(const HookSlot &slot) {
    return slot.abi_kind == "InstanceVoid0" ||
           slot.abi_kind == "InstanceVoid1" ||
           slot.abi_kind == "InstanceVoidInt1" ||
           slot.abi_kind == "InstanceVoidPtrFloatInt" ||
           slot.abi_kind == "InstanceVoid3" ||
           slot.abi_kind == "InstanceVoidBoolBoolPtrBool" ||
           slot.abi_kind == "InstanceVoidColor1" ||
           slot.abi_kind == "InstanceVoidIntBool" ||
           slot.abi_kind == "InstanceVoidPtrBool" ||
           slot.abi_kind == "InstanceBool1" ||
           slot.abi_kind == "InstanceBool2" ||
           slot.abi_kind == "InstanceBoolBoolInt" ||
           slot.abi_kind == "StaticVoid1" ||
           slot.abi_kind == "StaticIntFloatFloatBoolFloatFloatDouble";
}

HookSlotRuleRef make_rule_ref(const RuntimeBundle &bundle,
                              const RuntimeTarget &target,
                              const RuntimeRule &rule) {
    HookSlotRuleRef ref{
        .bundle_id = bundle.bundle_id,
        .target_id = target.id,
        .pc_mod_session = bundle.pc_mod_session,
        .rule_id = rule.id,
        .feature_id = rule.feature_id,
        .stage_code = rule.stage_code,
        .op_code = rule.op_code,
        .required_capabilities = rule.required_capabilities,
        .enabled = rule.default_enabled,
    };

    if (!rule.default_enabled) {
        ref.enabled = false;
        ref.disabled_reason = "disabled by recipe default";
    } else if ((rule.required_capabilities & ~g_state.approved_capabilities) != 0) {
        ref.enabled = false;
        ref.disabled_reason = "disabled by capability gate";
    } else if (rule.op_code == kRuleOpManagedEventCallback &&
               !parse_managed_event_rule_id(rule.id, &ref.managed_event_id)) {
        ref.enabled = false;
        ref.disabled_reason = "malformed managed event rule id";
    } else if (rule.op_code == kRuleOpManagedSynchronousPrefix &&
               !parse_managed_prefix_rule_id(rule.id, &ref.managed_prefix_id)) {
        ref.enabled = false;
        ref.disabled_reason = "malformed managed prefix rule id";
    } else if (rule.op_code == kRuleOpManagedRenderCallback &&
               !parse_managed_render_rule_id(rule.id, &ref.managed_prefix_id)) {
        ref.enabled = false;
        ref.disabled_reason = "malformed managed render rule id";
    }

    if (ref.managed_prefix_id != 0) {
        const auto mod_plan = g_managed_prefix_order_plans.find(bundle.mod_id);
        if (mod_plan != g_managed_prefix_order_plans.end()) {
            const auto metadata = mod_plan->second.find(ref.managed_prefix_id);
            if (metadata != mod_plan->second.end()) {
                ref.managed_prefix_priority = metadata->second.priority;
                ref.managed_prefix_registration_index = metadata->second.registration_index;
                ref.managed_prefix_owner = metadata->second.owner;
                ref.managed_prefix_before = metadata->second.before;
                ref.managed_prefix_after = metadata->second.after;
            }
        }
    }

    if (ref.managed_event_id != 0) {
        const auto mod_plan = g_managed_postfix_order_plans.find(bundle.mod_id);
        if (mod_plan != g_managed_postfix_order_plans.end()) {
            const auto metadata = mod_plan->second.find(ref.managed_event_id);
            if (metadata != mod_plan->second.end()) {
                ref.managed_event_priority = metadata->second.priority;
                ref.managed_event_registration_index = metadata->second.registration_index;
                ref.managed_event_owner = metadata->second.owner;
                ref.managed_event_before = metadata->second.before;
                ref.managed_event_after = metadata->second.after;
            }
        }
    }

    return ref;
}

void sort_rule_refs(std::vector<HookSlotRuleRef> &rules) {
    std::sort(rules.begin(), rules.end(), [](const HookSlotRuleRef &a, const HookSlotRuleRef &b) {
        if (a.bundle_id != b.bundle_id)
            return a.bundle_id < b.bundle_id;
        if (a.target_id != b.target_id)
            return a.target_id < b.target_id;
        return a.rule_id < b.rule_id;
    });
}

void add_rule_ref(HookSlot &slot, HookSlotRuleRef ref) {
    switch (ref.stage_code) {
        case 0:
            slot.before_rules.push_back(std::move(ref));
            break;
        case 1:
            slot.after_rules.push_back(std::move(ref));
            break;
        case 2:
            slot.replace_rules.push_back(std::move(ref));
            break;
        default:
            slot.status = "unsupported rule stage code";
            slot.state = SlotDisabledByCapability;
            break;
    }
}

int slot_rule_count(const HookSlot &slot) {
    return static_cast<int>(slot.before_rules.size() + slot.replace_rules.size() + slot.after_rules.size());
}

int slot_enabled_rule_count(const HookSlot &slot) {
    const auto enabled_count = [](const std::vector<HookSlotRuleRef> &rules) {
        return std::count_if(rules.begin(), rules.end(), [](const HookSlotRuleRef &rule) {
            return rule.enabled;
        });
    };
    return static_cast<int>(
        enabled_count(slot.before_rules) +
        enabled_count(slot.replace_rules) +
        enabled_count(slot.after_rules));
}

int enabled_rule_count(const std::vector<HookSlotRuleRef> &rules) {
    return static_cast<int>(std::count_if(rules.begin(), rules.end(), [](const HookSlotRuleRef &rule) {
        return rule.enabled;
    }));
}

bool is_first_dispatcher_op_supported(int op_code) {
    switch (op_code) {
        case kRuleOpOverlayShow:
        case kRuleOpOverlayShowPractice:
        case kRuleOpOverlayHandleStateChange:
        case kRuleOpOverlayHide:
        case kRuleOpOverlayUpdatePlayers:
        case kRuleOpPublishMarginSnapshot:
        case kRuleOpOverlayRecordHit:
        case kRuleOpOverlayResetJudgement:
        case kRuleOpOverlayRecordFloorMove:
        case kRuleOpOverlayRecordPlayerHit:
        case kRuleOpOverlayRecordDeath:
        case kRuleOpOverlayRecordHitTiming:
        case kRuleOpOverlayPollTelemetry:
        case kRuleOpResourceApplyEditorRabbit:
        case kRuleOpResourceApplyFloorColor:
        case kRuleOpResourceApplyPlanetColor:
        case kRuleOpResourceApplyLogoText:
        case kRuleOpManagedEventCallback:
        case kRuleOpGameplayAcceptedObserve:
            return true;
        default:
            return false;
    }
}

bool is_first_dispatcher_before_op_supported(int op_code) {
    switch (op_code) {
        case kRuleOpResourceSkipPlanetColorOriginal:
        case kRuleOpResourceOverridePlanetColorArg:
        case kRuleOpResourceSkipTileColorOriginal:
        case kRuleOpManagedSynchronousPrefix:
        case kRuleOpManagedRenderCallback:
            return true;
        default:
            return false;
    }
}

uint64_t build_before_op_mask(const HookSlot &slot) {
    uint64_t mask = 0;
    for (const auto &rule : slot.before_rules) {
        if (!rule.enabled)
            continue;
        if (rule.op_code >= 0 && rule.op_code < 63)
            mask |= 1ULL << static_cast<uint32_t>(rule.op_code);
    }
    return mask;
}

uint64_t build_after_op_mask(const HookSlot &slot) {
    uint64_t mask = 0;
    for (const auto &rule : slot.after_rules) {
        if (!rule.enabled)
            continue;
        if (rule.op_code >= 0 && rule.op_code < 63)
            mask |= 1ULL << static_cast<uint32_t>(rule.op_code);
    }
    return mask;
}

bool has_unsupported_first_dispatcher_op(const HookSlot &slot, std::string &reason) {
    for (const auto &rule : slot.before_rules) {
        if (!rule.enabled)
            continue;
        if (!is_first_dispatcher_before_op_supported(rule.op_code)) {
            reason = "unsupported first dispatcher before op code: " + std::to_string(rule.op_code);
            return true;
        }
    }

    for (const auto &rule : slot.after_rules) {
        if (!rule.enabled)
            continue;
        if (!is_first_dispatcher_op_supported(rule.op_code)) {
            reason = "unsupported first dispatcher op code: " + std::to_string(rule.op_code);
            return true;
        }
    }

    return false;
}

int find_bound_dispatcher_index_locked(const std::string &key) {
    int found = -1;
    for_each_dispatcher_runtime_slot([&](int index, DispatcherRuntimeSlot &runtime) {
        if (found < 0 && runtime.permanently_bound && runtime.bound_key == key)
            found = index;
    });
    return found;
}

void synchronize_bound_dispatchers_locked();

void rebuild_slots_locked() {
    std::map<std::string, HookSlot> previous_slots;
    for (const auto &slot : g_state.slots)
        previous_slots.emplace(slot.key, slot);

    std::vector<HookSlot> slots;
    std::map<std::string, size_t> slot_index;

    for (const auto &bundle : g_state.bundles) {
        for (const auto &target : bundle.targets) {
            const auto key = target_key(target);
            auto iter = slot_index.find(key);
            const bool is_new_slot = iter == slot_index.end();
            if (iter == slot_index.end()) {
                HookSlot slot;
                slot.key = key;
                slot.assembly_name = target.assembly_name;
                slot.namespace_name = target.namespace_name;
                slot.type_name = target.type_name;
                slot.method_name = target.method_name;
                slot.is_static = target.is_static;
                slot.generic_arity = target.generic_arity;
                slot.return_type = target.return_type;
                slot.parameter_types = target.parameter_types;
                slot.has_param_count = target.has_param_count;
                slot.param_count = target.param_count;
                slot.abi_kind = target.abi_kind;
                slot.status = "pending metadata resolve";
                iter = slot_index.emplace(key, slots.size()).first;
                slots.push_back(std::move(slot));
            }

            auto &slot = slots[iter->second];
            const auto previous = previous_slots.find(slot.key);
            if (is_new_slot) {
                const int bound_index = find_bound_dispatcher_index_locked(slot.key);
                if (bound_index >= 0) {
                    auto *runtime = dispatcher_runtime_slot(bound_index);
                    if (runtime == nullptr) {
                        slot.state = SlotFaulted;
                        slot.install_blocked = true;
                        slot.status = "persistent dispatcher page is unavailable";
                    } else {
                        slot.dispatcher_index = bound_index;
                        slot.function = runtime->bound_function;
                        slot.original = runtime->original.load(std::memory_order_acquire);
                        if (runtime->bound_abi_kind != slot.abi_kind) {
                            slot.state = SlotFaulted;
                            slot.install_blocked = true;
                            slot.status = "persistent hook ABI mismatch: " +
                                          runtime->bound_abi_kind + " vs " + slot.abi_kind;
                        } else {
                            slot.state = SlotHookInstalled;
                            slot.status = "hook installed (persistent binding reused)";
                        }
                    }
                } else if (previous != previous_slots.end() &&
                           (previous->second.state == SlotInstallFailed || previous->second.state == SlotFaulted)) {
                    slot.state = previous->second.state;
                    slot.function = previous->second.function;
                    slot.original = previous->second.original;
                    slot.dispatcher_index = previous->second.dispatcher_index;
                    slot.install_planned = previous->second.install_planned;
                    slot.install_blocked = previous->second.install_blocked;
                    slot.status = previous->second.status;
                }
            }

            if (slot.abi_kind != target.abi_kind) {
                slot.state = SlotFaulted;
                slot.install_blocked = true;
                slot.status = "conflicting ABI kinds for merged target: " +
                              slot.abi_kind + " vs " + target.abi_kind;
            }

            if (target.resolve_attempted)
                slot.resolve_attempted = true;
            if (target.resolved) {
                if (slot.function != nullptr && slot.function != target.function) {
                    slot.state = SlotInstallFailed;
                    slot.status = "conflicting runtime function pointers for merged target";
                } else if (slot.state != SlotInstallFailed && slot.state != SlotHookInstalled && slot.state != SlotFaulted) {
                    slot.function = target.function;
                    slot.state = SlotResolved;
                    slot.status = "resolved";
                }
            }

            if (target.resolve_attempted && !target.resolved && slot.state == SlotPendingResolve) {
                slot.resolve_failed = true;
                slot.status = target.resolve_error.empty() ? "metadata resolve failed" : target.resolve_error;
            }

            for (const auto &rule : target.rules)
                add_rule_ref(slot, make_rule_ref(bundle, target, rule));
        }
    }

    std::sort(slots.begin(), slots.end(), [](const HookSlot &a, const HookSlot &b) {
        return a.key < b.key;
    });

    uint32_t slot_id = 1;
    for (auto &slot : slots) {
        slot.slot_id = slot_id++;
        sort_rule_refs(slot.before_rules);
        sort_rule_refs(slot.replace_rules);
        sort_rule_refs(slot.after_rules);

        if (slot.state == SlotResolved &&
            slot_enabled_rule_count(slot) != 0 &&
            slot.function != nullptr) {
            uintptr_t protected_function = 0;
            if (!PC_COMPAT_RESOLVE_ADDRESS(
                    0,
                    0,
                    slot.slot_id,
                    0 |
                        0,
                    reinterpret_cast<uintptr_t>(slot.function),
                    &protected_function) ||
                protected_function !=
                    reinterpret_cast<uintptr_t>(slot.function)) {
                slot.resolve_failed = true;
                slot.state = SlotInstallFailed;
                slot.install_blocked = true;
                slot.status = "protected function descriptor resolve failed";
            } else {
                slot.function = reinterpret_cast<void *>(protected_function);
                slot.status = "resolved through protected function descriptor";
            }
        }

        if (slot.state == SlotResolved && slot_enabled_rule_count(slot) == 0) {
            slot.state = SlotDisabledByCapability;
            slot.status = "all rules disabled";
        }
    }

    g_state.slots = std::move(slots);
    synchronize_bound_dispatchers_locked();
}

void split_type_name(const std::string &full_name, std::string &namespaze, std::string &class_name) {
    const auto index = full_name.rfind('.');
    if (index == std::string::npos) {
        namespaze.clear();
        class_name = full_name;
        return;
    }

    namespaze = full_name.substr(0, index);
    class_name = full_name.substr(index + 1);
}

std::string normalize_assembly_name(std::string name) {
    const auto separator = name.find_last_of("/\\");
    if (separator != std::string::npos)
        name.erase(0, separator + 1);
    std::transform(name.begin(), name.end(), name.begin(), [](unsigned char value) {
        return static_cast<char>(std::tolower(value));
    });
    if (name.ends_with(".dll"))
        name.resize(name.size() - 4);
    return name;
}

struct MappedDynsymLookup {
    const char *soname = nullptr;
    const char *symbol = nullptr;
    void *address = nullptr;
};

uintptr_t mapped_dynamic_entry_address(const dl_phdr_info *info, uintptr_t value) {
    if (value == 0)
        return 0;
    const uintptr_t base = static_cast<uintptr_t>(info->dlpi_addr);
    if (base != 0 && value >= base && value < base + 0x100000000ULL)
        return value;
    return base + value;
}

size_t mapped_gnu_hash_symbol_count(const uint32_t *gnu_hash) {
    if (gnu_hash == nullptr)
        return 0;

    const uint32_t bucket_count = gnu_hash[0];
    const uint32_t symbol_offset = gnu_hash[1];
    const uint32_t bloom_size = gnu_hash[2];
    const auto *bloom = reinterpret_cast<const uintptr_t *>(gnu_hash + 4);
    const uint32_t *buckets = reinterpret_cast<const uint32_t *>(bloom + bloom_size);
    const uint32_t *chains = buckets + bucket_count;

    uint32_t max_symbol = 0;
    for (uint32_t index = 0; index < bucket_count; ++index)
        max_symbol = std::max(max_symbol, buckets[index]);
    if (max_symbol < symbol_offset)
        return symbol_offset;

    uint32_t chain_index = max_symbol - symbol_offset;
    while ((chains[chain_index] & 1u) == 0)
        ++chain_index;
    return static_cast<size_t>(symbol_offset + chain_index + 1);
}

int resolve_mapped_dynsym_callback(dl_phdr_info *info, size_t, void *data) {
    auto *lookup = static_cast<MappedDynsymLookup *>(data);
    if (lookup == nullptr || lookup->address != nullptr ||
        lookup->soname == nullptr || lookup->symbol == nullptr ||
        info == nullptr || info->dlpi_name == nullptr ||
        std::strstr(info->dlpi_name, lookup->soname) == nullptr) {
        return 0;
    }

    const ElfW(Dyn) *dynamic = nullptr;
    for (ElfW(Half) index = 0; index < info->dlpi_phnum; ++index) {
        if (info->dlpi_phdr[index].p_type == PT_DYNAMIC) {
            dynamic = reinterpret_cast<const ElfW(Dyn) *>(
                static_cast<uintptr_t>(info->dlpi_addr) +
                static_cast<uintptr_t>(info->dlpi_phdr[index].p_vaddr));
            break;
        }
    }
    if (dynamic == nullptr)
        return 0;

    const ElfW(Sym) *symbol_table = nullptr;
    const char *string_table = nullptr;
    const uint32_t *sysv_hash = nullptr;
    const uint32_t *gnu_hash = nullptr;
    size_t symbol_entry_size = sizeof(ElfW(Sym));
    size_t symbol_count = 0;
    for (const ElfW(Dyn) *entry = dynamic; entry->d_tag != DT_NULL; ++entry) {
        if (entry->d_tag == DT_SYMTAB) {
            symbol_table = reinterpret_cast<const ElfW(Sym) *>(
                mapped_dynamic_entry_address(info, static_cast<uintptr_t>(entry->d_un.d_ptr)));
        } else if (entry->d_tag == DT_STRTAB) {
            string_table = reinterpret_cast<const char *>(
                mapped_dynamic_entry_address(info, static_cast<uintptr_t>(entry->d_un.d_ptr)));
        } else if (entry->d_tag == DT_SYMENT) {
            symbol_entry_size = static_cast<size_t>(entry->d_un.d_val);
        } else if (entry->d_tag == DT_HASH) {
            sysv_hash = reinterpret_cast<const uint32_t *>(
                mapped_dynamic_entry_address(info, static_cast<uintptr_t>(entry->d_un.d_ptr)));
        } else if (entry->d_tag == DT_GNU_HASH) {
            gnu_hash = reinterpret_cast<const uint32_t *>(
                mapped_dynamic_entry_address(info, static_cast<uintptr_t>(entry->d_un.d_ptr)));
        }
    }

    if (symbol_table == nullptr || string_table == nullptr || symbol_entry_size == 0)
        return 0;
    if (sysv_hash != nullptr)
        symbol_count = sysv_hash[1];
    else if (gnu_hash != nullptr)
        symbol_count = mapped_gnu_hash_symbol_count(gnu_hash);
    if (symbol_count == 0)
        return 0;

    for (size_t index = 0; index < symbol_count; ++index) {
        const auto *symbol = reinterpret_cast<const ElfW(Sym) *>(
            reinterpret_cast<const char *>(symbol_table) + index * symbol_entry_size);
        if (ELF64_ST_TYPE(symbol->st_info) != STT_FUNC ||
            symbol->st_name == 0 || symbol->st_value == 0) {
            continue;
        }
        if (std::strcmp(string_table + symbol->st_name, lookup->symbol) == 0) {
            lookup->address = reinterpret_cast<void *>(
                static_cast<uintptr_t>(info->dlpi_addr) +
                static_cast<uintptr_t>(symbol->st_value));
            return 1;
        }
    }
    return 0;
}

void *resolve_mapped_il2cpp_symbol(const char *name) {
    MappedDynsymLookup lookup{
        .soname = "libil2cpp.so",
        .symbol = name,
    };
    dl_iterate_phdr(resolve_mapped_dynsym_callback, &lookup);
    return lookup.address;
}

void *resolve_il2cpp_symbol(const char *name) {
    if (void *mapped = resolve_mapped_il2cpp_symbol(name); mapped != nullptr)
        return mapped;
    return g_il2cpp_metadata.handle == nullptr
        ? nullptr
        : dlsym(g_il2cpp_metadata.handle, name);
}

template <typename T>
bool publish_protected_il2cpp_symbol(
    T &destination,
    const char *name,
    void *candidate,
    std::string &error) {
    uint32_t descriptor_slot = g_next_api_descriptor_slot.fetch_add(
        1, std::memory_order_relaxed);
    if (descriptor_slot == 0) {
        descriptor_slot = g_next_api_descriptor_slot.fetch_add(
            1, std::memory_order_relaxed);
    }
    uintptr_t protected_address = 0;
    if (!PC_COMPAT_RESOLVE_ADDRESS(
            0,
            0,
            descriptor_slot,
            0 |
                0,
            reinterpret_cast<uintptr_t>(candidate),
            &protected_address) ||
        protected_address != reinterpret_cast<uintptr_t>(candidate)) {
        destination = nullptr;
        if (error.empty()) {
            error = std::string(
                "protected IL2CPP metadata API descriptor failed: ") + name;
        }
        return false;
    }

    destination = reinterpret_cast<T>(protected_address);
    return true;
}

template <typename T>
bool load_il2cpp_symbol(T &destination, const char *name, std::string &error) {
    void *candidate = resolve_il2cpp_symbol(name);
    if (candidate == nullptr) {
        destination = nullptr;
        if (error.empty())
            error = std::string("missing IL2CPP metadata symbol: ") + name;
        return false;
    }
    return publish_protected_il2cpp_symbol(
        destination, name, candidate, error);
}

template <typename T>
bool load_optional_il2cpp_symbol(
    T &destination,
    const char *name,
    std::string &error) {
    void *candidate = resolve_il2cpp_symbol(name);
    if (candidate == nullptr) {
        destination = nullptr;
        return true;
    }
    return publish_protected_il2cpp_symbol(
        destination, name, candidate, error);
}

bool load_il2cpp_metadata_api_locked(std::string &error) {
    if (g_il2cpp_metadata.handle == nullptr) {
        g_il2cpp_metadata.handle = dlopen("libil2cpp.so", RTLD_NOW | RTLD_NOLOAD);
    }

    Il2CppMetadataApi resolved;
    resolved.handle = g_il2cpp_metadata.handle;
    bool complete = true;
    complete &= load_il2cpp_symbol(resolved.domain_get, "il2cpp_domain_get", error);
    complete &= load_il2cpp_symbol(resolved.get_corlib, "il2cpp_get_corlib", error);
    complete &= load_il2cpp_symbol(resolved.thread_attach, "il2cpp_thread_attach", error);
    complete &= load_il2cpp_symbol(
        resolved.domain_get_assemblies,
        "il2cpp_domain_get_assemblies",
        error);
    complete &= load_il2cpp_symbol(
        resolved.assembly_get_image,
        "il2cpp_assembly_get_image",
        error);
    complete &= load_il2cpp_symbol(resolved.image_get_name, "il2cpp_image_get_name", error);
    complete &= load_il2cpp_symbol(resolved.class_from_name, "il2cpp_class_from_name", error);
    complete &= load_il2cpp_symbol(resolved.object_get_class, "il2cpp_object_get_class", error);
    complete &= load_il2cpp_symbol(resolved.class_get_type, "il2cpp_class_get_type", error);
    complete &= load_il2cpp_symbol(resolved.type_get_object, "il2cpp_type_get_object", error);
    complete &= load_il2cpp_symbol(
        resolved.class_get_field_from_name,
        "il2cpp_class_get_field_from_name",
        error);
    complete &= load_il2cpp_symbol(resolved.class_get_methods, "il2cpp_class_get_methods", error);
    complete &= load_il2cpp_symbol(resolved.field_get_offset, "il2cpp_field_get_offset", error);
    complete &= load_il2cpp_symbol(
        resolved.field_static_get_value,
        "il2cpp_field_static_get_value",
        error);
    complete &= load_il2cpp_symbol(resolved.method_get_name, "il2cpp_method_get_name", error);
    complete &= load_il2cpp_symbol(
        resolved.method_get_param_count,
        "il2cpp_method_get_param_count",
        error);
    complete &= load_il2cpp_symbol(resolved.method_get_param, "il2cpp_method_get_param", error);
    complete &= load_il2cpp_symbol(
        resolved.method_get_return_type,
        "il2cpp_method_get_return_type",
        error);
    complete &= load_il2cpp_symbol(resolved.method_get_flags, "il2cpp_method_get_flags", error);
    complete &= load_il2cpp_symbol(
        resolved.method_is_generic,
        "il2cpp_method_is_generic",
        error);
    complete &= load_il2cpp_symbol(resolved.type_get_name, "il2cpp_type_get_name", error);
    complete &= load_il2cpp_symbol(resolved.runtime_invoke, "il2cpp_runtime_invoke", error);
    complete &= load_il2cpp_symbol(resolved.object_unbox, "il2cpp_object_unbox", error);
    complete &= load_il2cpp_symbol(resolved.string_new, "il2cpp_string_new", error);
    complete &= load_il2cpp_symbol(resolved.string_chars, "il2cpp_string_chars", error);
    complete &= load_il2cpp_symbol(resolved.string_length, "il2cpp_string_length", error);
    complete &= load_il2cpp_symbol(
        resolved.array_object_header_size,
        "il2cpp_array_object_header_size",
        error);
    complete &= load_il2cpp_symbol(
        resolved.array_length,
        "il2cpp_array_length",
        error);
    // Object/array allocation is presentation-only functionality.  Keep it
    // optional so a game build that omits one export can still load ordinary
    // fixed-op hook rules; the UnityMain factory will fail closed instead.
    complete &= load_optional_il2cpp_symbol(
        resolved.object_new,
        "il2cpp_object_new",
        error);
    complete &= load_optional_il2cpp_symbol(
        resolved.array_new,
        "il2cpp_array_new",
        error);
    complete &= load_il2cpp_symbol(resolved.gchandle_new, "il2cpp_gchandle_new", error);
    complete &= load_il2cpp_symbol(
        resolved.gchandle_get_target,
        "il2cpp_gchandle_get_target",
        error);
    complete &= load_il2cpp_symbol(resolved.gchandle_free, "il2cpp_gchandle_free", error);
    complete &= load_optional_il2cpp_symbol(
        resolved.free_memory,
        "il2cpp_free",
        error);
    if (!complete)
        return false;

    g_il2cpp_metadata = std::move(resolved);
    return true;
}

bool refresh_il2cpp_images_locked(std::string &error) {
    if (g_il2cpp_metadata.get_corlib == nullptr ||
        g_il2cpp_metadata.get_corlib() == nullptr) {
        error = "il2cpp_get_corlib returned null";
        return false;
    }

    void *domain = g_il2cpp_metadata.domain_get();
    if (domain == nullptr) {
        error = "il2cpp_domain_get returned null";
        return false;
    }
    g_il2cpp_metadata.thread_attach(domain);

    size_t assembly_count = 0;
    const void **assemblies = g_il2cpp_metadata.domain_get_assemblies(domain, &assembly_count);
    if (assemblies == nullptr || assembly_count == 0) {
        error = "il2cpp_domain_get_assemblies returned no assemblies";
        return false;
    }

    std::map<std::string, void *> images;
    for (size_t index = 0; index < assembly_count; ++index) {
        void *image = g_il2cpp_metadata.assembly_get_image(assemblies[index]);
        const char *image_name = image == nullptr ? nullptr : g_il2cpp_metadata.image_get_name(image);
        if (image == nullptr || image_name == nullptr || *image_name == '\0')
            continue;
        images[normalize_assembly_name(image_name)] = image;
    }
    if (images.find("assembly-csharp") == images.end()) {
        error = "Assembly-CSharp image not found";
        return false;
    }

    g_il2cpp_metadata.domain = domain;
    g_il2cpp_metadata.images = std::move(images);
    g_il2cpp_metadata.ready = true;

    // Foreign CoreCLR threads (mod workers) call libil2cpp exports directly;
    // attach them on entry or the Boehm GC aborts with "Collecting from
    // unknown thread" the first time one allocates. Fail-open: the guard is a
    // safety net and must not block metadata readiness.
    std::string guard_error;
    if (!starray::il2cpp_thread_guard::install(guard_error))
        LOGE("il2cpp thread guard install failed (fail-open): %s", guard_error.c_str());

    return true;
}

bool ensure_il2cpp_metadata(std::string &error) {
    std::lock_guard<std::mutex> resolve_guard(g_metadata_resolve_lock);
    if (g_il2cpp_metadata.ready) {
        g_il2cpp_metadata.thread_attach(g_il2cpp_metadata.domain);
        return true;
    }
    if (!load_il2cpp_metadata_api_locked(error))
        return false;
    return refresh_il2cpp_images_locked(error);
}

void *find_assembly_image(const std::string &name) {
    const auto iter = g_il2cpp_metadata.images.find(normalize_assembly_name(name));
    return iter == g_il2cpp_metadata.images.end() ? nullptr : iter->second;
}

void *find_class(void *image,
                 const std::string &namespace_name,
                 const std::string &type_name) {
    if (image == nullptr || g_il2cpp_metadata.class_from_name == nullptr)
        return nullptr;

    std::string namespaze = namespace_name;
    std::string class_name;
    if (namespaze.empty()) {
        split_type_name(type_name, namespaze, class_name);
    } else {
        const std::string prefix = namespaze + ".";
        class_name = type_name.starts_with(prefix)
            ? type_name.substr(prefix.size())
            : type_name;
    }
    return g_il2cpp_metadata.class_from_name(
        image,
        namespaze.c_str(),
        class_name.c_str());
}

int32_t find_field_offset(void *klass, const char *primary_name, const char *fallback_name) {
    if (klass == nullptr ||
        g_il2cpp_metadata.class_get_field_from_name == nullptr ||
        g_il2cpp_metadata.field_get_offset == nullptr) {
        return -1;
    }

    void *field = g_il2cpp_metadata.class_get_field_from_name(klass, primary_name);
    if (field == nullptr && fallback_name != nullptr)
        field = g_il2cpp_metadata.class_get_field_from_name(klass, fallback_name);
    if (field == nullptr)
        return -1;

    const size_t offset = g_il2cpp_metadata.field_get_offset(field);
    if (offset == 0 || offset > 0x10000u)
        return -1;
    uint32_t descriptor_slot = g_next_scalar_descriptor_slot.fetch_add(
        1, std::memory_order_relaxed);
    if (descriptor_slot == 0) {
        descriptor_slot = g_next_scalar_descriptor_slot.fetch_add(
            1, std::memory_order_relaxed);
    }
    uint64_t protected_offset = 0;
    if (!PC_COMPAT_RESOLVE_SCALAR(
            0,
            0,
            descriptor_slot,
            static_cast<uint64_t>(offset),
            &protected_offset) ||
        protected_offset != static_cast<uint64_t>(offset) ||
        protected_offset > static_cast<uint64_t>(INT32_MAX)) {
        return -1;
    }
    return static_cast<int32_t>(protected_offset);
}

bool resolve_margin_tracker_offsets(void *klass, std::string &error) {
    const int32_t percent_acc_offset = find_field_offset(
        klass,
        "<percentAcc>k__BackingField",
        "percentAcc");
    const int32_t percent_x_acc_offset = find_field_offset(
        klass,
        "<percentXAcc>k__BackingField",
        "percentXAcc");
    if (percent_acc_offset < 0 || percent_x_acc_offset < 0) {
        error = "scrMarginTracker accuracy field metadata is unavailable";
        return false;
    }

    g_margin_percent_acc_offset.store(percent_acc_offset, std::memory_order_release);
    g_margin_percent_x_acc_offset.store(percent_x_acc_offset, std::memory_order_release);
    LOGI("resolved scrMarginTracker accuracy fields percentAcc=0x%x percentXAcc=0x%x",
         percent_acc_offset,
         percent_x_acc_offset);
    return true;
}

bool resolve_hit_margin_snapshot_metadata(std::string &error) {
    if (g_margin_hit_counts_offset.load(std::memory_order_acquire) >= 0 &&
        g_mistakes_margin_trackers_field.load(std::memory_order_acquire) != nullptr) {
        return true;
    }

    std::lock_guard<std::mutex> guard(g_hit_margin_metadata_lock);
    if (g_margin_hit_counts_offset.load(std::memory_order_relaxed) >= 0 &&
        g_mistakes_margin_trackers_field.load(std::memory_order_relaxed) != nullptr) {
        return true;
    }
    if (!ensure_il2cpp_metadata(error))
        return false;

    void *image = find_assembly_image("Assembly-CSharp");
    void *tracker_class = find_class(image, "", "scrMarginTracker");
    void *mistakes_class = find_class(image, "", "scrMistakesManager");
    const int32_t counts_offset = find_field_offset(
        tracker_class,
        "hitMarginsCount",
        "<hitMarginsCount>k__BackingField");
    void *trackers_field = mistakes_class == nullptr
        ? nullptr
        : g_il2cpp_metadata.class_get_field_from_name(mistakes_class, "marginTrackers");
    if (counts_offset < 0 || trackers_field == nullptr) {
        error = "hit-margin snapshot metadata is unavailable";
        return false;
    }

    g_margin_hit_counts_offset.store(counts_offset, std::memory_order_release);
    g_mistakes_margin_trackers_field.store(trackers_field, std::memory_order_release);
    LOGI("resolved hit-margin snapshot metadata hitMarginsCount=0x%x", counts_offset);
    return true;
}

void *current_margin_tracker_from_static_array() {
    void *field = g_mistakes_margin_trackers_field.load(std::memory_order_acquire);
    if (field == nullptr ||
        g_il2cpp_metadata.field_static_get_value == nullptr ||
        g_il2cpp_metadata.array_length == nullptr ||
        g_il2cpp_metadata.array_object_header_size == nullptr) {
        return nullptr;
    }

    void *trackers = nullptr;
    g_il2cpp_metadata.field_static_get_value(field, &trackers);
    if (trackers == nullptr || g_il2cpp_metadata.array_length(trackers) == 0)
        return nullptr;

    const uint32_t header_size = g_il2cpp_metadata.array_object_header_size();
    if (header_size == 0)
        return nullptr;
    const auto *first = static_cast<const char *>(trackers) + header_size;
    return *reinterpret_cast<void *const *>(first);
}

bool read_hit_margin_counts(void *tracker,
                            std::array<int32_t, kHitMarginSnapshotMaxCounts> &counts,
                            uint32_t &length,
                            uint32_t &checksum) {
    const int32_t counts_offset =
        g_margin_hit_counts_offset.load(std::memory_order_acquire);
    void *count_array = nullptr;
    if (tracker == nullptr ||
        counts_offset < 0 ||
        !read_instance_value(tracker, counts_offset, count_array) ||
        count_array == nullptr ||
        g_il2cpp_metadata.array_length == nullptr ||
        g_il2cpp_metadata.array_object_header_size == nullptr) {
        return false;
    }

    const uintptr_t runtime_length = g_il2cpp_metadata.array_length(count_array);
    if (runtime_length == 0 || runtime_length > kHitMarginSnapshotMaxCounts)
        return false;
    const uint32_t header_size = g_il2cpp_metadata.array_object_header_size();
    if (header_size == 0)
        return false;

    length = static_cast<uint32_t>(runtime_length);
    const auto *source = reinterpret_cast<const int32_t *>(
        static_cast<const char *>(count_array) + header_size);
    checksum = 17;
    for (uint32_t index = 0; index < length; ++index) {
        counts[index] = source[index];
        checksum = checksum * 31u + static_cast<uint32_t>(counts[index]);
    }
    for (uint32_t index = length; index < counts.size(); ++index)
        counts[index] = 0;
    return true;
}

void commit_hit_margin_snapshot(void *tracker,
                                const std::array<int32_t, kHitMarginSnapshotMaxCounts> &counts,
                                uint32_t length,
                                uint32_t checksum,
                                bool valid) {
    std::lock_guard<std::mutex> guard(g_hit_margin_snapshot.publish_lock);
    const uintptr_t tracker_value = reinterpret_cast<uintptr_t>(tracker);
    bool changed = g_hit_margin_snapshot.valid.load(std::memory_order_relaxed) !=
                       static_cast<uint32_t>(valid) ||
                   g_hit_margin_snapshot.length.load(std::memory_order_relaxed) != length ||
                   g_hit_margin_snapshot.checksum.load(std::memory_order_relaxed) != checksum ||
                   g_hit_margin_snapshot.tracker.load(std::memory_order_relaxed) != tracker_value;
    if (!changed) {
        for (uint32_t index = 0; index < length; ++index) {
            if (g_hit_margin_snapshot.counts[index].load(std::memory_order_relaxed) != counts[index]) {
                changed = true;
                break;
            }
        }
    }
    if (!changed)
        return;

    g_hit_margin_snapshot.sequence.fetch_add(1, std::memory_order_acq_rel);
    g_hit_margin_snapshot.valid.store(valid ? 1u : 0u, std::memory_order_relaxed);
    g_hit_margin_snapshot.length.store(length, std::memory_order_relaxed);
    g_hit_margin_snapshot.checksum.store(checksum, std::memory_order_relaxed);
    g_hit_margin_snapshot.tracker.store(tracker_value, std::memory_order_relaxed);
    for (uint32_t index = 0; index < counts.size(); ++index)
        g_hit_margin_snapshot.counts[index].store(counts[index], std::memory_order_relaxed);
    g_hit_margin_snapshot.generation.fetch_add(1, std::memory_order_relaxed);
    g_hit_margin_snapshot.sequence.fetch_add(1, std::memory_order_release);
    g_margin_tracker_instance.store(tracker_value, std::memory_order_release);
}

void clear_hit_margin_snapshot();

bool publish_hit_margin_snapshot(void *preferred_tracker) {
    std::string error;
    if (!resolve_hit_margin_snapshot_metadata(error))
        return false;

    // A tracker-method receiver is the exact object the official method just
    // mutated. Static marginTrackers is authoritative only when no typed receiver
    // exists (session start / SetPlayerCount / fallback polling).
    void *tracker = preferred_tracker;
    if (tracker == nullptr)
        tracker = current_margin_tracker_from_static_array();
    if (tracker == nullptr) {
        clear_hit_margin_snapshot();
        return true;
    }

    std::array<int32_t, kHitMarginSnapshotMaxCounts> counts{};
    uint32_t length = 0;
    uint32_t checksum = 0;
    if (!read_hit_margin_counts(tracker, counts, length, checksum))
        return false;
    commit_hit_margin_snapshot(tracker, counts, length, checksum, true);
    return true;
}

void clear_hit_margin_snapshot() {
    const std::array<int32_t, kHitMarginSnapshotMaxCounts> counts{};
    commit_hit_margin_snapshot(nullptr, counts, 0, 0, false);
    g_last_hit_margin_authoritative_publish_ms.store(0, std::memory_order_release);
}

enum class AbiValueClass : uint8_t {
    Void,
    AnyGp,
    Gp32,
    Bool,
    Float32,
    Float64,
    ColorValue,
    IndirectStruct,
};

struct DispatcherAbiSpec {
    bool is_static = false;
    AbiValueClass return_type = AbiValueClass::Void;
    std::vector<AbiValueClass> params;
};

bool get_dispatcher_abi_spec(const std::string &abi_kind, DispatcherAbiSpec &spec) {
    if (abi_kind == "InstanceVoid0")
        spec = {false, AbiValueClass::Void, {}};
    else if (abi_kind == "InstanceVoid1")
        spec = {false, AbiValueClass::Void, {AbiValueClass::AnyGp}};
    else if (abi_kind == "InstanceVoidInt1")
        spec = {false, AbiValueClass::Void, {AbiValueClass::Gp32}};
    else if (abi_kind == "InstanceVoidPtrFloatInt")
        spec = {false, AbiValueClass::Void, {AbiValueClass::AnyGp, AbiValueClass::Float32, AbiValueClass::Gp32}};
    else if (abi_kind == "InstanceVoid3")
        spec = {false, AbiValueClass::Void, {AbiValueClass::AnyGp, AbiValueClass::AnyGp, AbiValueClass::AnyGp}};
    else if (abi_kind == "InstanceVoidBoolBoolPtrBool")
        spec = {false, AbiValueClass::Void, {AbiValueClass::Bool, AbiValueClass::Bool, AbiValueClass::AnyGp, AbiValueClass::Bool}};
    else if (abi_kind == "InstanceVoidColor1")
        spec = {false, AbiValueClass::Void, {AbiValueClass::ColorValue}};
    else if (abi_kind == "InstanceVoidIntBool")
        spec = {false, AbiValueClass::Void, {AbiValueClass::Gp32, AbiValueClass::Bool}};
    else if (abi_kind == "InstanceVoidPtrBool")
        spec = {false, AbiValueClass::Void, {AbiValueClass::IndirectStruct, AbiValueClass::Bool}};
    else if (abi_kind == "InstanceBool1")
        spec = {false, AbiValueClass::Bool, {AbiValueClass::Bool}};
    else if (abi_kind == "InstanceBool2")
        spec = {false, AbiValueClass::Bool, {AbiValueClass::Gp32, AbiValueClass::Bool}};
    else if (abi_kind == "InstanceBoolBoolInt")
        spec = {false, AbiValueClass::Bool, {AbiValueClass::Bool, AbiValueClass::Gp32}};
    else if (abi_kind == "StaticVoid1")
        spec = {true, AbiValueClass::Void, {AbiValueClass::Gp32}};
    else if (abi_kind == "StaticIntFloatFloatBoolFloatFloatDouble")
        spec = {true, AbiValueClass::Gp32, {
            AbiValueClass::Float32,
            AbiValueClass::Float32,
            AbiValueClass::Bool,
            AbiValueClass::Float32,
            AbiValueClass::Float32,
            AbiValueClass::Float64,
        }};
    else
        return false;
    return true;
}

std::string canonical_identity_type_name(std::string name) {
    const auto first = name.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
        return {};
    const auto last = name.find_last_not_of(" \t\r\n");
    name = name.substr(first, last - first + 1);
    std::replace(name.begin(), name.end(), '/', '.');
    std::replace(name.begin(), name.end(), '+', '.');

    std::string prefix_modifier;
    if (name.starts_with("ref ")) {
        name.erase(0, 4);
        prefix_modifier = "&";
    } else if (name.starts_with("out ")) {
        name.erase(0, 4);
        prefix_modifier = "&";
    } else if (name.starts_with("in ")) {
        name.erase(0, 3);
        prefix_modifier = "&";
    }

    const auto suffix_pos = name.find_first_of("[&*");
    const std::string suffix = suffix_pos == std::string::npos
        ? std::string{}
        : name.substr(suffix_pos);
    std::string base = suffix_pos == std::string::npos
        ? name
        : name.substr(0, suffix_pos);

    static const std::map<std::string, std::string> aliases{
        {"void", "System.Void"}, {"Void", "System.Void"},
        {"bool", "System.Boolean"}, {"Boolean", "System.Boolean"},
        {"byte", "System.Byte"}, {"Byte", "System.Byte"},
        {"sbyte", "System.SByte"}, {"SByte", "System.SByte"},
        {"char", "System.Char"}, {"Char", "System.Char"},
        {"short", "System.Int16"}, {"Int16", "System.Int16"},
        {"ushort", "System.UInt16"}, {"UInt16", "System.UInt16"},
        {"int", "System.Int32"}, {"Int32", "System.Int32"},
        {"uint", "System.UInt32"}, {"UInt32", "System.UInt32"},
        {"long", "System.Int64"}, {"Int64", "System.Int64"},
        {"ulong", "System.UInt64"}, {"UInt64", "System.UInt64"},
        {"float", "System.Single"}, {"Single", "System.Single"},
        {"double", "System.Double"}, {"Double", "System.Double"},
        {"string", "System.String"}, {"String", "System.String"},
        {"object", "System.Object"}, {"Object", "System.Object"},
        {"Enum", "System.Enum"}, {"Action", "System.Action"},
    };
    const auto alias = aliases.find(base);
    if (alias != aliases.end())
        base = alias->second;
    return base + suffix + prefix_modifier;
}

std::string normalize_runtime_type_name(std::string name) {
    while (!name.empty() && (name.back() == '&' || name.back() == '*'))
        name.pop_back();
    return name;
}

bool validate_method_identity(const ResolvedMethodMetadata *method,
                              const RuntimeTarget &target,
                              std::string &error) {
    if (method == nullptr) {
        error = "runtime method metadata is null";
        return false;
    }
    if (target.generic_arity != 0) {
        error = "generic target arity is not supported by the strict Android resolver";
        return false;
    }
    if (method->is_generic) {
        error = "generic method does not match genericArity=0";
        return false;
    }
    if (method->is_static != target.is_static) {
        error = std::string("static/instance mismatch; expected ") +
                (target.is_static ? "static" : "instance");
        return false;
    }
    if (canonical_identity_type_name(method->return_type) !=
        canonical_identity_type_name(target.return_type)) {
        error = "return type mismatch: expected " + target.return_type +
                " got " + method->return_type;
        return false;
    }
    if (method->params.size() != target.parameter_types.size()) {
        error = "parameter count mismatch: expected " +
                std::to_string(target.parameter_types.size()) + " got " +
                std::to_string(method->params.size());
        return false;
    }
    for (size_t index = 0; index < target.parameter_types.size(); ++index) {
        if (canonical_identity_type_name(method->params[index]) ==
            canonical_identity_type_name(target.parameter_types[index])) {
            continue;
        }
        error = "parameter type mismatch at index " + std::to_string(index) +
                ": expected " + target.parameter_types[index] +
                " got " + method->params[index];
        return false;
    }
    return true;
}

bool is_gp32_runtime_type(const std::string &raw_name) {
    const auto name = normalize_runtime_type_name(raw_name);
    static const std::set<std::string> known_gp32_types{
        "System.Boolean", "Boolean", "bool",
        "System.Byte", "Byte", "System.SByte", "SByte", "System.Char", "Char",
        "System.Int16", "Int16", "System.UInt16", "UInt16",
        "System.Int32", "Int32", "System.UInt32", "UInt32",
        "HitMargin", "InputEventState"
    };
    if (known_gp32_types.count(name) != 0)
        return true;
    const auto separator = name.rfind('.');
    return separator != std::string::npos && known_gp32_types.count(name.substr(separator + 1)) != 0;
}

bool runtime_type_matches(const std::string &raw_name, AbiValueClass expected) {
    const auto name = normalize_runtime_type_name(raw_name);
    switch (expected) {
        case AbiValueClass::Void:
            return name == "System.Void" || name == "Void" || name == "void";
        case AbiValueClass::Bool:
            return name == "System.Boolean" || name == "Boolean" || name == "bool";
        case AbiValueClass::Float32:
            return name == "System.Single" || name == "Single" || name == "float";
        case AbiValueClass::Float64:
            return name == "System.Double" || name == "Double" || name == "double";
        case AbiValueClass::Gp32:
            return is_gp32_runtime_type(name);
        case AbiValueClass::ColorValue:
            return name == "UnityEngine.Color" || name == "Color";
        case AbiValueClass::IndirectStruct:
            return name == "PlanetColor" || name.ends_with(".PlanetColor");
        case AbiValueClass::AnyGp:
            return !name.empty() &&
                   !runtime_type_matches(name, AbiValueClass::Void) &&
                   !runtime_type_matches(name, AbiValueClass::Float32) &&
                   !runtime_type_matches(name, AbiValueClass::Float64);
    }
    return false;
}

std::string describe_method_signature(const ResolvedMethodMetadata *method) {
    if (method == nullptr)
        return "<null>";
    std::ostringstream output;
    output << (method->is_static ? "static " : "instance ")
           << (method->return_type.empty() ? "<null>" : method->return_type)
           << " " << method->name << "(";
    for (size_t index = 0; index < method->params.size(); ++index) {
        if (index != 0)
            output << ",";
        output << (method->params[index].empty() ? "<null>" : method->params[index]);
    }
    output << ")";
    return output.str();
}

bool validate_method_abi(const ResolvedMethodMetadata *method,
                          const std::string &abi_kind,
                          std::string &error) {
    DispatcherAbiSpec spec;
    if (!get_dispatcher_abi_spec(abi_kind, spec)) {
        error = "unsupported dispatcher ABI: " + abi_kind;
        return false;
    }
    if (method == nullptr || method->return_type.empty()) {
        error = "runtime method metadata is incomplete";
        return false;
    }
    if (method->is_static != spec.is_static) {
        error = std::string("static/instance mismatch; expected ") +
                (spec.is_static ? "static" : "instance");
        return false;
    }
    if (!runtime_type_matches(method->return_type, spec.return_type)) {
        error = "return type mismatch: " + method->return_type;
        return false;
    }
    if (method->params.size() != spec.params.size()) {
        error = "parameter count mismatch: expected " + std::to_string(spec.params.size()) +
                " got " + std::to_string(method->params.size());
        return false;
    }
    for (size_t index = 0; index < spec.params.size(); ++index) {
        const auto &arg_type = method->params[index];
        if (arg_type.empty()) {
            error = "parameter metadata is incomplete at index " + std::to_string(index);
            return false;
        }
        if (!runtime_type_matches(arg_type, spec.params[index])) {
            error = "parameter type mismatch at index " + std::to_string(index) +
                    ": " + arg_type;
            return false;
        }
    }
    return true;
}

std::string get_runtime_type_name(const void *type) {
    if (type == nullptr || g_il2cpp_metadata.type_get_name == nullptr)
        return {};
    char *raw_name = g_il2cpp_metadata.type_get_name(type);
    if (raw_name == nullptr)
        return {};
    std::string result(raw_name);
    if (g_il2cpp_metadata.free_memory != nullptr)
        g_il2cpp_metadata.free_memory(raw_name);
    return result;
}

bool read_method_metadata(const void *method_info, ResolvedMethodMetadata &method) {
    if (method_info == nullptr)
        return false;

    const char *name = g_il2cpp_metadata.method_get_name(method_info);
    if (name == nullptr)
        return false;

    method = ResolvedMethodMetadata{};
    method.method_info = method_info;
    method.function = static_cast<const Il2CppMethodInfoHead *>(method_info)->method_pointer;
    method.name = name;
    method.return_type = get_runtime_type_name(
        g_il2cpp_metadata.method_get_return_type(method_info));
    uint32_t implementation_flags = 0;
    const uint32_t flags = g_il2cpp_metadata.method_get_flags(method_info, &implementation_flags);
    method.is_static = (flags & 0x0010u) != 0;
    method.is_generic = g_il2cpp_metadata.method_is_generic(method_info);

    const uint32_t param_count = g_il2cpp_metadata.method_get_param_count(method_info);
    method.params.reserve(param_count);
    for (uint32_t index = 0; index < param_count; ++index) {
        method.params.push_back(get_runtime_type_name(
            g_il2cpp_metadata.method_get_param(method_info, index)));
    }
    return true;
}

bool find_method(void *klass,
                 const RuntimeTarget &target,
                 ResolvedMethodMetadata &selected,
                 std::string &error) {
    if (klass == nullptr)
        return false;
    if (target.generic_arity != 0) {
        error = "generic target arity is not supported by the strict Android resolver";
        return false;
    }

    std::vector<ResolvedMethodMetadata> candidates;
    void *iterator = nullptr;
    for (;;) {
        const void *method_info = g_il2cpp_metadata.class_get_methods(klass, &iterator);
        if (method_info == nullptr)
            break;
        ResolvedMethodMetadata method;
        if (!read_method_metadata(method_info, method) || method.name != target.method_name)
            continue;
        if (target.has_param_count && static_cast<int>(method.params.size()) != target.param_count)
            continue;
        candidates.push_back(std::move(method));
    }
    if (candidates.empty())
        return false;

    std::vector<size_t> compatible;
    std::vector<std::string> rejected;
    for (size_t index = 0; index < candidates.size(); ++index) {
        std::string identity_error;
        if (!validate_method_identity(&candidates[index], target, identity_error)) {
            rejected.push_back(
                describe_method_signature(&candidates[index]) + ": " + identity_error);
            continue;
        }

        std::string abi_error;
        if (validate_method_abi(&candidates[index], target.abi_kind, abi_error))
            compatible.push_back(index);
        else
            rejected.push_back(describe_method_signature(&candidates[index]) + ": " + abi_error);
    }

    if (compatible.size() == 1) {
        selected = std::move(candidates[compatible[0]]);
        return true;
    }
    if (compatible.size() > 1) {
        error = "multiple full-signature matches for " + target.type_name + "." + target.method_name;
        return false;
    }

    error = "no full-signature and ABI-compatible overload for " + target.type_name + "." + target.method_name;
    if (!rejected.empty())
        error += "; " + rejected[0];
    return false;
}

struct ResourceRuntimeCache {
    bool attempted = false;
    bool ready = false;
    std::string error;
    void *assembly_csharp = nullptr;
    void *unity_core = nullptr;
    void *unity_ui = nullptr;
    void *scr_planet_klass = nullptr;
    void *scr_floor_klass = nullptr;
    void *planet_renderer_klass = nullptr;
    void *floor_renderer_klass = nullptr;
    void *ado_base_klass = nullptr;
    void *rd_constants_klass = nullptr;
    void *planet_sprite_klass = nullptr;
    void *component_klass = nullptr;
    void *game_object_klass = nullptr;
    void *object_klass = nullptr;
    void *transform_klass = nullptr;
    void *rect_transform_klass = nullptr;
    void *scr_controller_klass = nullptr;
    void *scr_logo_text_klass = nullptr;
    void *scn_editor_klass = nullptr;
    void *image_klass = nullptr;
    void *text_klass = nullptr;
    void *graphic_klass = nullptr;
    void *rdc_klass = nullptr;
    void *ado_base_get_gc = nullptr;
    void *planet_sprite_set_sprite = nullptr;
    void *planet_renderer_disable_all = nullptr;
    void *planet_renderer_set_planet_color = nullptr;
    void *planet_renderer_set_tail_color = nullptr;
    void *planet_renderer_load_planet_color = nullptr;
    void *scr_floor_set_color = nullptr;
    void *component_get_game_object = nullptr;
    void *component_get_transform = nullptr;
    void *game_object_get_component = nullptr;
    void *game_object_get_transform = nullptr;
    void *game_object_compare_tag = nullptr;
    void *game_object_set_active = nullptr;
    void *object_instantiate = nullptr;
    void *object_set_name = nullptr;
    void *transform_get_parent = nullptr;
    void *transform_find = nullptr;
    void *transform_set_parent = nullptr;
    void *rect_transform_get_anchored_position = nullptr;
    void *rect_transform_set_anchored_position = nullptr;
    void *text_set_text = nullptr;
    void *text_set_font_size = nullptr;
    void *graphic_set_color = nullptr;
    void *rect_transform_type_object = nullptr;
    void *text_type_object = nullptr;
    void *scr_controller_get_instance = nullptr;
    void *scr_controller_get_coop_mode = nullptr;
    void *scr_controller_get_percent_complete = nullptr;
    void *scr_logo_text_color_logo = nullptr;
    void *scr_logo_text_update_colors = nullptr;
    void *scn_editor_otto_update = nullptr;
    void *image_get_sprite = nullptr;
    void *image_set_sprite = nullptr;
    void *image_set_color = nullptr;
    void *rdc_get_auto = nullptr;
    int32_t scr_planet_planet_renderer_offset = -1;
    int32_t scr_planet_is_red_offset = -1;
    int32_t planet_renderer_sprite_offset = -1;
    int32_t rd_constants_tex_planet_white_offset = -1;
    int32_t scr_floor_floor_renderer_offset = -1;
    int32_t scn_editor_auto_image_offset = -1;
};

struct TelemetryRuntimeCache {
    bool attempted = false;
    bool ready = false;
    std::string error;
    void *assembly_csharp = nullptr;
    void *unity_core = nullptr;
    void *unity_audio = nullptr;
    void *scr_controller_klass = nullptr;
    void *scr_conductor_klass = nullptr;
    void *scr_level_maker_klass = nullptr;
    void *scr_floor_klass = nullptr;
    void *ffx_checkpoint_klass = nullptr;
    void *ado_base_klass = nullptr;
    void *rdc_klass = nullptr;
    void *planetary_system_klass = nullptr;
    void *component_klass = nullptr;
    void *time_klass = nullptr;
    void *audio_source_klass = nullptr;
    void *audio_clip_klass = nullptr;
    void *scr_controller_get_instance = nullptr;
    void *scr_controller_get_planetary_system = nullptr;
    void *scr_controller_get_curr_floor = nullptr;
    void *scr_controller_get_paused = nullptr;
    void *scr_conductor_get_instance = nullptr;
    void *scr_conductor_get_songposition_minusi = nullptr;
    void *scr_level_maker_get_instance = nullptr;
    void *rdc_get_auto = nullptr;
    void *ado_base_get_controller = nullptr;
    void *ado_base_get_conductor = nullptr;
    void *ado_base_get_is_scn_game = nullptr;
    void *ado_base_get_is_official_level = nullptr;
    void *ado_base_get_current_level = nullptr;
    void *ado_base_get_level_path = nullptr;
    void *component_get_component = nullptr;
    void *time_get_time = nullptr;
    void *time_get_time_scale = nullptr;
    void *time_get_frame_count = nullptr;
    void *audio_source_get_time = nullptr;
    void *audio_source_get_clip = nullptr;
    void *audio_source_get_pitch = nullptr;
    void *audio_clip_get_length = nullptr;
    void *scr_controller_checkpoints_used_field = nullptr;
    void *ffx_checkpoint_type_object = nullptr;
    int32_t scr_controller_current_seq_id_offset = -1;
    int32_t scr_controller_current_state_offset = -1;
    int32_t scr_controller_no_fail_offset = -1;
    int32_t scr_controller_first_floor_offset = -1;
    int32_t scr_conductor_song_offset = -1;
    int32_t scr_conductor_add_offset_offset = -1;
    int32_t scr_conductor_is_game_world_offset = -1;
    int32_t scr_level_maker_list_floors_offset = -1;
    int32_t scr_floor_seq_id_offset = -1;
    int32_t scr_floor_entry_time_offset = -1;
    int32_t planetary_system_speed_offset = -1;
    int32_t list_items_offset = -1;
    int32_t list_size_offset = -1;
};

std::mutex g_resource_cache_lock;
std::condition_variable g_resource_cache_condition;
ResourceRuntimeCache g_resource_cache;
bool g_resource_cache_building = false;
std::mutex g_telemetry_cache_lock;
std::condition_variable g_telemetry_cache_condition;
TelemetryRuntimeCache g_telemetry_cache;
bool g_telemetry_cache_building = false;

bool ensure_resource_runtime_cache(std::string &error);
bool ensure_telemetry_runtime_cache(std::string &error);

bool resource_color_equal(const ColorValue &left, const ColorValue &right) {
    return left.r == right.r &&
           left.g == right.g &&
           left.b == right.b &&
           left.a == right.a;
}

ResourceEffectiveState select_resource_effective_state_locked() {
    ResourceEffectiveState selected;
    for (const auto &[owner, contribution] : g_resource_contributions) {
        if (contribution.feature_mask == 0)
            continue;
        if (!selected.present ||
            contribution.registration_sequence >
                selected.contribution.registration_sequence) {
            selected.present = true;
            selected.owner = owner;
            selected.contribution = contribution;
        }
    }
    return selected;
}

bool resource_feature_enabled(
    const ResourceEffectiveState &state,
    uint32_t feature) {
    return state.present && (state.contribution.feature_mask & feature) != 0;
}

uint32_t resource_transition_mask(
    const ResourceEffectiveState &previous,
    const ResourceEffectiveState &next) {
    const bool owner_changed =
        previous.present != next.present ||
        (previous.present &&
         !resource_owner_key_equal(previous.owner, next.owner));
    uint32_t mask = 0;

    if (owner_changed ||
        resource_feature_enabled(previous, kResourceContributionRabbit) !=
            resource_feature_enabled(next, kResourceContributionRabbit)) {
        if (resource_feature_enabled(previous, kResourceContributionRabbit) ||
            resource_feature_enabled(next, kResourceContributionRabbit)) {
            mask |= kResourceRestoreRabbit;
        }
    }

    const bool planet_changed =
        owner_changed ||
        resource_feature_enabled(previous, kResourceContributionPlanet) !=
            resource_feature_enabled(next, kResourceContributionPlanet) ||
        !resource_color_equal(
            previous.contribution.planet_color,
            next.contribution.planet_color) ||
        !resource_color_equal(
            previous.contribution.title_color,
            next.contribution.title_color) ||
        previous.contribution.resource_pack_name !=
            next.contribution.resource_pack_name;
    if (planet_changed && (previous.present || next.present))
        mask |= kResourceRestorePlanet;

    const bool tile_changed =
        owner_changed ||
        resource_feature_enabled(previous, kResourceContributionTile) !=
            resource_feature_enabled(next, kResourceContributionTile) ||
        !resource_color_equal(
            previous.contribution.tile_color,
            next.contribution.tile_color);
    if (tile_changed &&
        (resource_feature_enabled(previous, kResourceContributionTile) ||
         resource_feature_enabled(next, kResourceContributionTile))) {
        mask |= kResourceRestoreTile;
    }
    return mask;
}

uint32_t publish_resource_effective_state_locked() {
    const ResourceEffectiveState previous = g_resource_effective_state;
    const ResourceEffectiveState next = select_resource_effective_state_locked();
    const uint32_t transition_mask = resource_transition_mask(previous, next);
    g_resource_effective_state = next;

    const ResourceContribution &contribution = next.contribution;
    g_resource_change_rabbit.store(
        resource_feature_enabled(next, kResourceContributionRabbit) ? 1u : 0u,
        std::memory_order_release);
    g_resource_change_ball_color.store(
        resource_feature_enabled(next, kResourceContributionPlanet) ? 1u : 0u,
        std::memory_order_release);
    g_resource_change_tile_color.store(
        resource_feature_enabled(next, kResourceContributionTile) ? 1u : 0u,
        std::memory_order_release);
    g_resource_planet_r.store(contribution.planet_color.r, std::memory_order_release);
    g_resource_planet_g.store(contribution.planet_color.g, std::memory_order_release);
    g_resource_planet_b.store(contribution.planet_color.b, std::memory_order_release);
    g_resource_planet_a.store(contribution.planet_color.a, std::memory_order_release);
    g_resource_title_r.store(contribution.title_color.r, std::memory_order_release);
    g_resource_title_g.store(contribution.title_color.g, std::memory_order_release);
    g_resource_title_b.store(contribution.title_color.b, std::memory_order_release);
    g_resource_title_a.store(contribution.title_color.a, std::memory_order_release);
    g_resource_tile_r.store(contribution.tile_color.r, std::memory_order_release);
    g_resource_tile_g.store(contribution.tile_color.g, std::memory_order_release);
    g_resource_tile_b.store(contribution.tile_color.b, std::memory_order_release);
    g_resource_tile_a.store(contribution.tile_color.a, std::memory_order_release);

    g_resource_state_mod_id = next.present ? next.owner.mod_id : std::string{};
    g_resource_state_generation = next.present
        ? next.owner.session_generation
        : 0;
    const std::string resource_pack_name = next.present
        ? contribution.resource_pack_name
        : std::string{};
    g_resource_pack_name = resource_pack_name;
    if (transition_mask != 0) {
        g_resource_pending_restore_mask.fetch_or(
            transition_mask,
            std::memory_order_release);
    }
    return transition_mask;
}

bool refresh_resource_rabbit_sprite_projection_locked() {
    ResourceOwnerKey active_owner;
    bool active = false;
    {
        // Lock ordering is asset -> state. State publishers release their lock
        // before requesting a projection refresh.
        std::lock_guard<std::mutex> state_guard(g_resource_state_lock);
        active = resource_feature_enabled(
            g_resource_effective_state,
            kResourceContributionRabbit);
        if (active)
            active_owner = g_resource_effective_state.owner;
    }

    void *next_handle = nullptr;
    if (active) {
        auto sprite = g_resource_sprite_contributions.find(active_owner);
        if (sprite == g_resource_sprite_contributions.end() &&
            active_owner.session_generation <= 0) {
            for (auto candidate = g_resource_sprite_contributions.begin();
                 candidate != g_resource_sprite_contributions.end();
                 ++candidate) {
                if (candidate->first.mod_id != active_owner.mod_id)
                    continue;
                if (sprite == g_resource_sprite_contributions.end() ||
                    candidate->second.registration_sequence >
                        sprite->second.registration_sequence) {
                    sprite = candidate;
                }
            }
        }
        if (sprite != g_resource_sprite_contributions.end()) {
            next_handle = sprite->second.handle;
            active_owner = sprite->first;
        }
    }
    const bool changed = next_handle != g_resource_rabbit_sprite_handle;
    g_resource_rabbit_sprite_handle = next_handle;
    g_resource_rabbit_sprite_mod_id = next_handle != nullptr
        ? active_owner.mod_id
        : std::string{};
    g_resource_rabbit_sprite_generation = next_handle != nullptr
        ? active_owner.session_generation
        : 0;
    if (changed) {
        g_resource_pending_restore_mask.fetch_or(
            kResourceRestoreRabbit,
            std::memory_order_release);
    }
    return changed;
}

bool refresh_resource_rabbit_sprite_projection() {
    std::lock_guard<std::mutex> guard(g_resource_asset_lock);
    return refresh_resource_rabbit_sprite_projection_locked();
}

ColorValue resource_planet_color() {
    return {
        g_resource_planet_r.load(std::memory_order_acquire),
        g_resource_planet_g.load(std::memory_order_acquire),
        g_resource_planet_b.load(std::memory_order_acquire),
        g_resource_planet_a.load(std::memory_order_acquire),
    };
}

ColorValue resource_title_color() {
    return {
        g_resource_title_r.load(std::memory_order_acquire),
        g_resource_title_g.load(std::memory_order_acquire),
        g_resource_title_b.load(std::memory_order_acquire),
        g_resource_title_a.load(std::memory_order_acquire),
    };
}

ColorValue resource_tile_color() {
    return {
        g_resource_tile_r.load(std::memory_order_acquire),
        g_resource_tile_g.load(std::memory_order_acquire),
        g_resource_tile_b.load(std::memory_order_acquire),
        g_resource_tile_a.load(std::memory_order_acquire),
    };
}

ColorValue resource_rabbit_color(bool auto_enabled) {
    return auto_enabled
        ? ColorValue{0.5703125f, 0.0f, 1.0f, 1.0f}
        : ColorValue{0.19607843f, 0.0f, 0.32941177f, 1.0f};
}

bool resource_change_rabbit_enabled() {
    return g_resource_change_rabbit.load(std::memory_order_acquire) != 0;
}

bool resource_change_ball_color_enabled() {
    return g_resource_change_ball_color.load(std::memory_order_acquire) != 0;
}

bool resource_change_tile_color_enabled() {
    return g_resource_change_tile_color.load(std::memory_order_acquire) != 0;
}

bool resource_has_active_contribution() {
    std::lock_guard<std::mutex> guard(g_resource_state_lock);
    return g_resource_effective_state.present;
}

bool find_method_by_identity(void *klass,
                             const char *type_name,
                             const char *method_name,
                             const char *return_type,
                             std::initializer_list<const char *> param_types,
                             bool is_static,
                             void **method_info,
                             std::string &error) {
    RuntimeTarget target;
    target.assembly_name = "Assembly-CSharp";
    target.type_name = type_name;
    target.method_name = method_name;
    target.is_static = is_static;
    target.generic_arity = 0;
    target.return_type = return_type;
    target.parameter_types.assign(param_types.begin(), param_types.end());
    target.has_param_count = true;
    target.param_count = static_cast<int>(target.parameter_types.size());
    target.abi_kind = "Unknown";

    std::vector<ResolvedMethodMetadata> candidates;
    void *iterator = nullptr;
    for (;;) {
        const void *candidate_info = g_il2cpp_metadata.class_get_methods(klass, &iterator);
        if (candidate_info == nullptr)
            break;
        ResolvedMethodMetadata method;
        if (!read_method_metadata(candidate_info, method) || method.name != method_name)
            continue;
        if (method.params.size() != target.parameter_types.size())
            continue;

        std::string identity_error;
        if (validate_method_identity(&method, target, identity_error))
            candidates.push_back(std::move(method));
    }

    if (candidates.size() != 1) {
        error = std::string("method identity resolve failed: ") + type_name + "." + method_name +
                " matches=" + std::to_string(candidates.size());
        return false;
    }

    *method_info = const_cast<void *>(candidates[0].method_info);
    return true;
}

void *invoke_il2cpp_method(void *method_info, void *instance, void **args) {
    if (method_info == nullptr || g_il2cpp_metadata.runtime_invoke == nullptr)
        return nullptr;
    void *exception = nullptr;
    void *result = g_il2cpp_metadata.runtime_invoke(method_info, instance, args, &exception);
    if (exception != nullptr)
        return nullptr;
    return result;
}

bool invoke_void_color(void *method_info, void *instance, ColorValue color) {
    void *args[] = {&color};
    invoke_il2cpp_method(method_info, instance, args);
    return true;
}

bool invoke_bool_noargs(void *method_info, bool fallback = false) {
    void *result = invoke_il2cpp_method(method_info, nullptr, nullptr);
    if (result == nullptr || g_il2cpp_metadata.object_unbox == nullptr)
        return fallback;
    void *boxed = g_il2cpp_metadata.object_unbox(result);
    return boxed != nullptr ? *reinterpret_cast<uint8_t *>(boxed) != 0 : fallback;
}

bool resource_auto_enabled() {
    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return false;
    void *get_auto = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        get_auto = g_resource_cache.rdc_get_auto;
    }
    return invoke_bool_noargs(get_auto, false);
}

void *invoke_object_noargs(void *method_info, void *instance = nullptr) {
    return invoke_il2cpp_method(method_info, instance, nullptr);
}

float invoke_float_noargs(void *method_info, void *instance, float fallback = 0.0f) {
    void *result = invoke_il2cpp_method(method_info, instance, nullptr);
    if (result == nullptr || g_il2cpp_metadata.object_unbox == nullptr)
        return fallback;
    void *boxed = g_il2cpp_metadata.object_unbox(result);
    return boxed != nullptr ? *reinterpret_cast<float *>(boxed) : fallback;
}

int32_t invoke_int32_noargs(void *method_info, void *instance, int32_t fallback = 0) {
    void *result = invoke_il2cpp_method(method_info, instance, nullptr);
    if (result == nullptr || g_il2cpp_metadata.object_unbox == nullptr)
        return fallback;
    void *boxed = g_il2cpp_metadata.object_unbox(result);
    return boxed != nullptr ? *reinterpret_cast<int32_t *>(boxed) : fallback;
}

double invoke_double_noargs(void *method_info, void *instance, double fallback = 0.0) {
    void *result = invoke_il2cpp_method(method_info, instance, nullptr);
    if (result == nullptr || g_il2cpp_metadata.object_unbox == nullptr)
        return fallback;
    void *boxed = g_il2cpp_metadata.object_unbox(result);
    return boxed != nullptr ? *reinterpret_cast<double *>(boxed) : fallback;
}

bool invoke_bool_noargs_on(void *method_info, void *instance, bool fallback = false) {
    void *result = invoke_il2cpp_method(method_info, instance, nullptr);
    if (result == nullptr || g_il2cpp_metadata.object_unbox == nullptr)
        return fallback;
    void *boxed = g_il2cpp_metadata.object_unbox(result);
    return boxed != nullptr ? *reinterpret_cast<uint8_t *>(boxed) != 0 : fallback;
}

void append_utf8_codepoint(std::string &output, uint32_t codepoint) {
    if (codepoint <= 0x7fu) {
        output.push_back(static_cast<char>(codepoint));
    } else if (codepoint <= 0x7ffu) {
        output.push_back(static_cast<char>(0xc0u | (codepoint >> 6u)));
        output.push_back(static_cast<char>(0x80u | (codepoint & 0x3fu)));
    } else if (codepoint <= 0xffffu) {
        output.push_back(static_cast<char>(0xe0u | (codepoint >> 12u)));
        output.push_back(static_cast<char>(0x80u | ((codepoint >> 6u) & 0x3fu)));
        output.push_back(static_cast<char>(0x80u | (codepoint & 0x3fu)));
    } else {
        output.push_back(static_cast<char>(0xf0u | (codepoint >> 18u)));
        output.push_back(static_cast<char>(0x80u | ((codepoint >> 12u) & 0x3fu)));
        output.push_back(static_cast<char>(0x80u | ((codepoint >> 6u) & 0x3fu)));
        output.push_back(static_cast<char>(0x80u | (codepoint & 0x3fu)));
    }
}

std::string managed_string_to_utf8(void *managed_string) {
    if (managed_string == nullptr ||
        g_il2cpp_metadata.string_chars == nullptr ||
        g_il2cpp_metadata.string_length == nullptr) {
        return {};
    }

    const char16_t *chars = g_il2cpp_metadata.string_chars(managed_string);
    const int32_t length = g_il2cpp_metadata.string_length(managed_string);
    if (chars == nullptr || length <= 0)
        return {};

    std::string output;
    output.reserve(static_cast<size_t>(length));
    for (int32_t index = 0; index < length; ++index) {
        uint32_t codepoint = chars[index];
        if (codepoint >= 0xd800u && codepoint <= 0xdbffu && index + 1 < length) {
            const uint32_t low = chars[index + 1];
            if (low >= 0xdc00u && low <= 0xdfffu) {
                codepoint = 0x10000u + ((codepoint - 0xd800u) << 10u) + (low - 0xdc00u);
                ++index;
            }
        }
        if (codepoint >= 0xd800u && codepoint <= 0xdfffu)
            codepoint = 0xfffdu;
        append_utf8_codepoint(output, codepoint);
    }
    return output;
}

std::string invoke_string_noargs(void *method_info, void *instance = nullptr) {
    return managed_string_to_utf8(invoke_il2cpp_method(method_info, instance, nullptr));
}

void *read_object_field(void *instance, int32_t offset) {
    if (instance == nullptr || offset <= 0)
        return nullptr;
    void *value = nullptr;
    std::memcpy(&value, static_cast<const char *>(instance) + offset, sizeof(value));
    return value;
}

void apply_resource_planet_white_texture(void *renderer) {
    if (renderer == nullptr)
        return;

    void *get_gc = nullptr;
    void *set_sprite = nullptr;
    int32_t sprite_offset = -1;
    int32_t tex_planet_white_offset = -1;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        get_gc = g_resource_cache.ado_base_get_gc;
        set_sprite = g_resource_cache.planet_sprite_set_sprite;
        sprite_offset = g_resource_cache.planet_renderer_sprite_offset;
        tex_planet_white_offset = g_resource_cache.rd_constants_tex_planet_white_offset;
    }

    void *planet_sprite = read_object_field(renderer, sprite_offset);
    void *gc = invoke_object_noargs(get_gc);
    void *white_texture = read_object_field(gc, tex_planet_white_offset);
    if (planet_sprite == nullptr || white_texture == nullptr || set_sprite == nullptr)
        return;

    void *args[] = {white_texture};
    invoke_il2cpp_method(set_sprite, planet_sprite, args);
}

bool compare_tag_with_method(void *object, void *compare_tag_method, const char *tag) {
    if (object == nullptr || compare_tag_method == nullptr || tag == nullptr || g_il2cpp_metadata.string_new == nullptr)
        return false;
    void *tag_string = g_il2cpp_metadata.string_new(tag);
    if (tag_string == nullptr)
        return false;
    void *args[] = {tag_string};
    void *result = invoke_il2cpp_method(compare_tag_method, object, args);
    if (result == nullptr || g_il2cpp_metadata.object_unbox == nullptr)
        return false;
    void *boxed = g_il2cpp_metadata.object_unbox(result);
    return boxed != nullptr && *reinterpret_cast<uint8_t *>(boxed) != 0;
}

bool resource_floor_is_beat(void *floor) {
    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return false;
    void *component_get_game_object = nullptr;
    void *game_object_compare_tag = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        if (!g_resource_cache.ready)
            return false;
        component_get_game_object = g_resource_cache.component_get_game_object;
        game_object_compare_tag = g_resource_cache.game_object_compare_tag;
    }
    void *game_object = invoke_il2cpp_method(component_get_game_object, floor, nullptr);
    return compare_tag_with_method(game_object, game_object_compare_tag, "Beat");
}

bool resource_is_coop_mode() {
    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return false;
    void *method = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        if (!g_resource_cache.ready || g_resource_cache.scr_controller_get_coop_mode == nullptr)
            return false;
        method = g_resource_cache.scr_controller_get_coop_mode;
    }
    return invoke_bool_noargs(method, false);
}

bool read_controller_percent_complete(void *controller, float &progress) {
    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return false;
    void *method = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        if (!g_resource_cache.ready || g_resource_cache.scr_controller_get_percent_complete == nullptr)
            return false;
        method = g_resource_cache.scr_controller_get_percent_complete;
    }

    progress = invoke_float_noargs(method, controller, progress);
    return std::isfinite(progress);
}

void publish_controller_progress_snapshot() {
    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return;

    void *get_instance = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        if (!g_resource_cache.ready || g_resource_cache.scr_controller_get_instance == nullptr)
            return;
        get_instance = g_resource_cache.scr_controller_get_instance;
    }

    void *controller = invoke_object_noargs(get_instance);
    float progress = bits_to_float(active_overlay_state().progress_bits.load(std::memory_order_acquire));
    if (!read_controller_percent_complete(controller, progress))
        return;
    progress = std::clamp(progress, 0.0f, 1.0f);
    active_overlay_state().progress_bits.store(float_to_bits(progress), std::memory_order_release);
}

bool ensure_resource_runtime_cache(std::string &error) {
    {
        std::unique_lock<std::mutex> guard(g_resource_cache_lock);
        while (g_resource_cache_building)
            g_resource_cache_condition.wait(guard);

        if (g_resource_cache.ready)
            return true;
        if (g_resource_cache.attempted) {
            error = g_resource_cache.error;
            return false;
        }

        g_resource_cache_building = true;
    }

    ResourceRuntimeCache cache;
    cache.attempted = true;

    if (!ensure_il2cpp_metadata(cache.error)) {
        {
            std::lock_guard<std::mutex> guard(g_resource_cache_lock);
            g_resource_cache = cache;
            g_resource_cache_building = false;
        }
        g_resource_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.assembly_csharp = find_assembly_image("Assembly-CSharp");
    cache.unity_core = find_assembly_image("UnityEngine.CoreModule");
    cache.unity_ui = find_assembly_image("UnityEngine.UI");
    if (cache.assembly_csharp == nullptr || cache.unity_core == nullptr) {
        cache.error = "required images for ResourceChanger are unavailable";
        {
            std::lock_guard<std::mutex> guard(g_resource_cache_lock);
            g_resource_cache = cache;
            g_resource_cache_building = false;
        }
        g_resource_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.scr_planet_klass = find_class(cache.assembly_csharp, "", "scrPlanet");
    cache.scr_floor_klass = find_class(cache.assembly_csharp, "", "scrFloor");
    cache.planet_renderer_klass = find_class(cache.assembly_csharp, "", "PlanetRenderer");
    cache.floor_renderer_klass = find_class(cache.assembly_csharp, "", "FloorRenderer");
    cache.ado_base_klass = find_class(cache.assembly_csharp, "", "ADOBase");
    cache.rd_constants_klass = find_class(cache.assembly_csharp, "", "RDConstants");
    cache.planet_sprite_klass = find_class(cache.assembly_csharp, "", "PlanetSprite");
    cache.scr_controller_klass = find_class(cache.assembly_csharp, "", "scrController");
    cache.scr_logo_text_klass = find_class(cache.assembly_csharp, "", "scrLogoText");
    cache.scn_editor_klass = find_class(cache.assembly_csharp, "", "scnEditor");
    cache.rdc_klass = find_class(cache.assembly_csharp, "", "RDC");
    cache.component_klass = find_class(cache.unity_core, "UnityEngine", "Component");
    cache.game_object_klass = find_class(cache.unity_core, "UnityEngine", "GameObject");
    cache.object_klass = find_class(cache.unity_core, "UnityEngine", "Object");
    cache.transform_klass = find_class(cache.unity_core, "UnityEngine", "Transform");
    cache.rect_transform_klass = find_class(cache.unity_core, "UnityEngine", "RectTransform");
    cache.image_klass = find_class(cache.unity_ui, "UnityEngine.UI", "Image");
    cache.text_klass = find_class(cache.unity_ui, "UnityEngine.UI", "Text");
    cache.graphic_klass = find_class(cache.unity_ui, "UnityEngine.UI", "Graphic");
    if (cache.scr_planet_klass == nullptr || cache.scr_floor_klass == nullptr ||
        cache.planet_renderer_klass == nullptr || cache.floor_renderer_klass == nullptr ||
        cache.ado_base_klass == nullptr || cache.rd_constants_klass == nullptr ||
        cache.planet_sprite_klass == nullptr ||
        cache.component_klass == nullptr || cache.game_object_klass == nullptr) {
        cache.error = "required classes for ResourceChanger are unavailable";
        {
            std::lock_guard<std::mutex> guard(g_resource_cache_lock);
            g_resource_cache = cache;
            g_resource_cache_building = false;
        }
        g_resource_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.scr_planet_planet_renderer_offset = find_field_offset(cache.scr_planet_klass, "planetRenderer", nullptr);
    cache.scr_planet_is_red_offset = find_field_offset(cache.scr_planet_klass, "isRed", nullptr);
    cache.scr_floor_floor_renderer_offset = find_field_offset(cache.scr_floor_klass, "floorRenderer", nullptr);
    cache.planet_renderer_sprite_offset = find_field_offset(cache.planet_renderer_klass, "sprite", nullptr);
    cache.rd_constants_tex_planet_white_offset = find_field_offset(cache.rd_constants_klass, "tex_planetWhite", nullptr);
    cache.scn_editor_auto_image_offset = find_field_offset(cache.scn_editor_klass, "autoImage", nullptr);
    if (cache.scr_planet_planet_renderer_offset < 0 || cache.scr_floor_floor_renderer_offset < 0 ||
        cache.planet_renderer_sprite_offset < 0 || cache.rd_constants_tex_planet_white_offset < 0) {
        cache.error = "required ResourceChanger field offsets are unavailable";
        {
            std::lock_guard<std::mutex> guard(g_resource_cache_lock);
            g_resource_cache = cache;
            g_resource_cache_building = false;
        }
        g_resource_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    bool ok = true;
    ok &= find_method_by_identity(cache.ado_base_klass, "ADOBase", "get_gc", "RDConstants", {}, true, &cache.ado_base_get_gc, cache.error);
    ok &= find_method_by_identity(cache.planet_sprite_klass, "PlanetSprite", "set_sprite", "System.Void", {"UnityEngine.Texture"}, false, &cache.planet_sprite_set_sprite, cache.error);
    ok &= find_method_by_identity(cache.planet_renderer_klass, "PlanetRenderer", "DisableAllSpecialPlanets", "System.Void", {}, false, &cache.planet_renderer_disable_all, cache.error);
    ok &= find_method_by_identity(cache.planet_renderer_klass, "PlanetRenderer", "SetPlanetColor", "System.Void", {"UnityEngine.Color"}, false, &cache.planet_renderer_set_planet_color, cache.error);
    ok &= find_method_by_identity(cache.planet_renderer_klass, "PlanetRenderer", "SetTailColor", "System.Void", {"UnityEngine.Color"}, false, &cache.planet_renderer_set_tail_color, cache.error);
    ok &= find_method_by_identity(cache.planet_renderer_klass, "PlanetRenderer", "LoadPlanetColor", "System.Void", {"System.Boolean"}, false, &cache.planet_renderer_load_planet_color, cache.error);
    ok &= find_method_by_identity(cache.scr_floor_klass, "scrFloor", "SetColor", "System.Void", {"UnityEngine.Color"}, false, &cache.scr_floor_set_color, cache.error);
    ok &= find_method_by_identity(cache.component_klass, "UnityEngine.Component", "get_gameObject", "UnityEngine.GameObject", {}, false, &cache.component_get_game_object, cache.error);
    ok &= find_method_by_identity(cache.component_klass, "UnityEngine.Component", "get_transform", "UnityEngine.Transform", {}, false, &cache.component_get_transform, cache.error);
    ok &= find_method_by_identity(cache.game_object_klass, "UnityEngine.GameObject", "GetComponent", "UnityEngine.Component", {"System.Type"}, false, &cache.game_object_get_component, cache.error);
    ok &= find_method_by_identity(cache.game_object_klass, "UnityEngine.GameObject", "get_transform", "UnityEngine.Transform", {}, false, &cache.game_object_get_transform, cache.error);
    ok &= find_method_by_identity(cache.game_object_klass, "UnityEngine.GameObject", "CompareTag", "System.Boolean", {"System.String"}, false, &cache.game_object_compare_tag, cache.error);
    if (cache.scr_controller_klass != nullptr) {
        std::string instance_error;
        if (!find_method_by_identity(cache.scr_controller_klass, "scrController", "get_instance", "scrController", {}, true, &cache.scr_controller_get_instance, instance_error)) {
            LOGI("HUD scrController.instance getter unavailable: %s", instance_error.c_str());
        }
        std::string coop_error;
        if (!find_method_by_identity(cache.scr_controller_klass, "scrController", "get_coopMode", "System.Boolean", {}, true, &cache.scr_controller_get_coop_mode, coop_error)) {
            LOGI("ResourceChanger coop-mode getter unavailable: %s", coop_error.c_str());
        }
        std::string progress_error;
        if (!find_method_by_identity(cache.scr_controller_klass, "scrController", "get_percentComplete", "System.Single", {}, false, &cache.scr_controller_get_percent_complete, progress_error)) {
            LOGI("HUD percentComplete getter unavailable: %s", progress_error.c_str());
        }
    }
    if (cache.scr_logo_text_klass != nullptr) {
        std::string logo_error;
        if (!find_method_by_identity(cache.scr_logo_text_klass, "scrLogoText", "ColorLogo", "System.Void", {"System.Nullable`1<UnityEngine.Color>", "System.Boolean"}, false, &cache.scr_logo_text_color_logo, logo_error)) {
            LOGI("ResourceChanger ColorLogo unavailable: %s", logo_error.c_str());
        }
        if (!find_method_by_identity(cache.scr_logo_text_klass, "scrLogoText", "UpdateColors", "System.Void", {}, false, &cache.scr_logo_text_update_colors, logo_error)) {
            LOGI("ResourceChanger UpdateColors unavailable: %s", logo_error.c_str());
        }
    }
    if (cache.rdc_klass != nullptr) {
        std::string auto_error;
        if (!find_method_by_identity(cache.rdc_klass, "RDC", "get_auto", "System.Boolean", {}, true, &cache.rdc_get_auto, auto_error))
            LOGI("ResourceChanger RDC.auto getter unavailable: %s", auto_error.c_str());
    }
    if (cache.scn_editor_klass != nullptr) {
        std::string editor_error;
        if (!find_method_by_identity(cache.scn_editor_klass, "scnEditor", "OttoUpdate", "System.Void", {}, false, &cache.scn_editor_otto_update, editor_error))
            LOGI("ResourceChanger OttoUpdate unavailable: %s", editor_error.c_str());
    }
    if (cache.object_klass != nullptr && cache.transform_klass != nullptr &&
        cache.rect_transform_klass != nullptr && cache.text_klass != nullptr &&
        cache.graphic_klass != nullptr) {
        std::string logo_ui_error;
        bool logo_ok = true;
        logo_ok &= find_method_by_identity(cache.game_object_klass, "UnityEngine.GameObject", "SetActive", "System.Void", {"System.Boolean"}, false, &cache.game_object_set_active, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.object_klass, "UnityEngine.Object", "Instantiate", "UnityEngine.Object", {"UnityEngine.Object"}, true, &cache.object_instantiate, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.object_klass, "UnityEngine.Object", "set_name", "System.Void", {"System.String"}, false, &cache.object_set_name, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.transform_klass, "UnityEngine.Transform", "get_parent", "UnityEngine.Transform", {}, false, &cache.transform_get_parent, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.transform_klass, "UnityEngine.Transform", "Find", "UnityEngine.Transform", {"System.String"}, false, &cache.transform_find, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.transform_klass, "UnityEngine.Transform", "SetParent", "System.Void", {"UnityEngine.Transform", "System.Boolean"}, false, &cache.transform_set_parent, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.rect_transform_klass, "UnityEngine.RectTransform", "get_anchoredPosition", "UnityEngine.Vector2", {}, false, &cache.rect_transform_get_anchored_position, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.rect_transform_klass, "UnityEngine.RectTransform", "set_anchoredPosition", "System.Void", {"UnityEngine.Vector2"}, false, &cache.rect_transform_set_anchored_position, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.text_klass, "UnityEngine.UI.Text", "set_text", "System.Void", {"System.String"}, false, &cache.text_set_text, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.text_klass, "UnityEngine.UI.Text", "set_fontSize", "System.Void", {"System.Int32"}, false, &cache.text_set_font_size, logo_ui_error);
        logo_ok &= find_method_by_identity(cache.graphic_klass, "UnityEngine.UI.Graphic", "set_color", "System.Void", {"UnityEngine.Color"}, false, &cache.graphic_set_color, logo_ui_error);
        const void *rect_transform_type = g_il2cpp_metadata.class_get_type != nullptr
            ? g_il2cpp_metadata.class_get_type(cache.rect_transform_klass)
            : nullptr;
        const void *text_type = g_il2cpp_metadata.class_get_type != nullptr
            ? g_il2cpp_metadata.class_get_type(cache.text_klass)
            : nullptr;
        cache.rect_transform_type_object = rect_transform_type != nullptr && g_il2cpp_metadata.type_get_object != nullptr
            ? g_il2cpp_metadata.type_get_object(rect_transform_type)
            : nullptr;
        cache.text_type_object = text_type != nullptr && g_il2cpp_metadata.type_get_object != nullptr
            ? g_il2cpp_metadata.type_get_object(text_type)
            : nullptr;
        if (!logo_ok || cache.rect_transform_type_object == nullptr || cache.text_type_object == nullptr)
            LOGI("ResourceChanger logo UI bridge unavailable: %s", logo_ui_error.c_str());
    }
    if (cache.image_klass != nullptr &&
        cache.scn_editor_auto_image_offset >= 0) {
        std::string rabbit_error;
        const bool get_ok = find_method_by_identity(
            cache.image_klass,
            "UnityEngine.UI.Image",
            "get_sprite",
            "UnityEngine.Sprite",
            {},
            false,
            &cache.image_get_sprite,
            rabbit_error);
        const bool set_ok = find_method_by_identity(
            cache.image_klass,
            "UnityEngine.UI.Image",
            "set_sprite",
            "System.Void",
            {"UnityEngine.Sprite"},
            false,
            &cache.image_set_sprite,
            rabbit_error);
        const bool color_ok = find_method_by_identity(
            cache.image_klass,
            "UnityEngine.UI.Image",
            "set_color",
            "System.Void",
            {"UnityEngine.Color"},
            false,
            &cache.image_set_color,
            rabbit_error);
        if (!get_ok || !set_ok || !color_ok)
            LOGI("ResourceChanger rabbit bridge unavailable: %s", rabbit_error.c_str());
    }

    if (!ok) {
        if (cache.error.empty())
            cache.error = "required ResourceChanger methods are unavailable";
        {
            std::lock_guard<std::mutex> guard(g_resource_cache_lock);
            g_resource_cache = cache;
            g_resource_cache_building = false;
        }
        g_resource_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.ready = true;
    cache.error.clear();
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        g_resource_cache = cache;
        g_resource_cache_building = false;
    }
    g_resource_cache_condition.notify_all();
    return true;
}

bool invoke_logo_color(void *method_info,
                       void *instance,
                       ColorValue color,
                       bool is_fire) {
    if (method_info == nullptr || instance == nullptr)
        return false;
    NullableColorValue nullable_color;
    nullable_color.has_value = 1;
    nullable_color.value = color;
    void *args[] = {&nullable_color, &is_fire};
    return invoke_il2cpp_method(method_info, instance, args) == nullptr;
}

bool ensure_telemetry_runtime_cache(std::string &error) {
    {
        std::unique_lock<std::mutex> guard(g_telemetry_cache_lock);
        while (g_telemetry_cache_building)
            g_telemetry_cache_condition.wait(guard);

        if (g_telemetry_cache.ready)
            return true;
        if (g_telemetry_cache.attempted) {
            error = g_telemetry_cache.error;
            return false;
        }
        g_telemetry_cache_building = true;
    }

    TelemetryRuntimeCache cache;
    cache.attempted = true;
    const auto commit_failure = [&]() {
        std::lock_guard<std::mutex> guard(g_telemetry_cache_lock);
        g_telemetry_cache = cache;
        g_telemetry_cache_building = false;
    };

    if (!ensure_il2cpp_metadata(cache.error)) {
        commit_failure();
        g_telemetry_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.assembly_csharp = find_assembly_image("Assembly-CSharp");
    cache.unity_core = find_assembly_image("UnityEngine.CoreModule");
    cache.unity_audio = find_assembly_image("UnityEngine.AudioModule");
    if (cache.assembly_csharp == nullptr) {
        cache.error = "Assembly-CSharp image for overlay telemetry is unavailable";
        commit_failure();
        g_telemetry_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.scr_controller_klass = find_class(cache.assembly_csharp, "", "scrController");
    cache.scr_conductor_klass = find_class(cache.assembly_csharp, "", "scrConductor");
    cache.scr_level_maker_klass = find_class(cache.assembly_csharp, "", "scrLevelMaker");
    cache.scr_floor_klass = find_class(cache.assembly_csharp, "", "scrFloor");
    cache.ffx_checkpoint_klass = find_class(cache.assembly_csharp, "", "ffxCheckpoint");
    cache.ado_base_klass = find_class(cache.assembly_csharp, "", "ADOBase");
    cache.rdc_klass = find_class(cache.assembly_csharp, "", "RDC");
    cache.planetary_system_klass = find_class(cache.assembly_csharp, "", "PlanetarySystem");
    cache.component_klass = cache.unity_core == nullptr
        ? nullptr
        : find_class(cache.unity_core, "UnityEngine", "Component");
    cache.time_klass = cache.unity_core == nullptr
        ? nullptr
        : find_class(cache.unity_core, "UnityEngine", "Time");
    cache.audio_source_klass = cache.unity_audio == nullptr
        ? nullptr
        : find_class(cache.unity_audio, "UnityEngine", "AudioSource");
    cache.audio_clip_klass = cache.unity_audio == nullptr
        ? nullptr
        : find_class(cache.unity_audio, "UnityEngine", "AudioClip");
    if (cache.scr_controller_klass == nullptr ||
        cache.scr_conductor_klass == nullptr ||
        cache.scr_level_maker_klass == nullptr ||
        cache.scr_floor_klass == nullptr ||
        cache.ado_base_klass == nullptr) {
        cache.error = "core gameplay classes for overlay telemetry are unavailable";
        commit_failure();
        g_telemetry_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    cache.scr_controller_current_seq_id_offset =
        find_field_offset(cache.scr_controller_klass, "currentSeqID", nullptr);
    cache.scr_controller_current_state_offset =
        find_field_offset(cache.scr_controller_klass, "currentState", nullptr);
    cache.scr_controller_no_fail_offset =
        find_field_offset(cache.scr_controller_klass, "noFail", nullptr);
    cache.scr_controller_first_floor_offset =
        find_field_offset(cache.scr_controller_klass, "firstFloor", nullptr);
    cache.scr_conductor_song_offset =
        find_field_offset(cache.scr_conductor_klass, "song", nullptr);
    cache.scr_conductor_add_offset_offset =
        find_field_offset(cache.scr_conductor_klass, "addoffset", nullptr);
    cache.scr_conductor_is_game_world_offset =
        find_field_offset(cache.scr_conductor_klass, "isGameWorld", nullptr);
    cache.scr_level_maker_list_floors_offset =
        find_field_offset(cache.scr_level_maker_klass, "listFloors", nullptr);
    cache.scr_floor_seq_id_offset =
        find_field_offset(cache.scr_floor_klass, "seqID", nullptr);
    cache.scr_floor_entry_time_offset =
        find_field_offset(cache.scr_floor_klass, "entryTime", nullptr);
    cache.planetary_system_speed_offset =
        find_field_offset(cache.planetary_system_klass, "speed", nullptr);
    cache.scr_controller_checkpoints_used_field =
        g_il2cpp_metadata.class_get_field_from_name(
            cache.scr_controller_klass,
            "checkpointsUsed");
    const void *checkpoint_type = cache.ffx_checkpoint_klass == nullptr
        ? nullptr
        : g_il2cpp_metadata.class_get_type(cache.ffx_checkpoint_klass);
    cache.ffx_checkpoint_type_object = checkpoint_type == nullptr
        ? nullptr
        : g_il2cpp_metadata.type_get_object(checkpoint_type);

    if (cache.scr_controller_current_seq_id_offset < 0 ||
        cache.scr_controller_current_state_offset < 0 ||
        cache.scr_controller_no_fail_offset < 0 ||
        cache.scr_conductor_is_game_world_offset < 0 ||
        cache.scr_level_maker_list_floors_offset < 0 ||
        cache.scr_floor_seq_id_offset < 0 ||
        cache.scr_floor_entry_time_offset < 0) {
        cache.error = "core gameplay fields for overlay telemetry are unavailable";
        commit_failure();
        g_telemetry_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    bool ok = true;
    ok &= find_method_by_identity(cache.scr_controller_klass, "scrController", "get_instance", "scrController", {}, true, &cache.scr_controller_get_instance, cache.error);
    ok &= find_method_by_identity(cache.scr_controller_klass, "scrController", "get_paused", "System.Boolean", {}, false, &cache.scr_controller_get_paused, cache.error);
    ok &= find_method_by_identity(cache.scr_conductor_klass, "scrConductor", "get_instance", "scrConductor", {}, true, &cache.scr_conductor_get_instance, cache.error);
    ok &= find_method_by_identity(cache.scr_level_maker_klass, "scrLevelMaker", "get_instance", "scrLevelMaker", {}, true, &cache.scr_level_maker_get_instance, cache.error);
    ok &= find_method_by_identity(cache.ado_base_klass, "ADOBase", "get_controller", "scrController", {}, true, &cache.ado_base_get_controller, cache.error);
    ok &= find_method_by_identity(cache.ado_base_klass, "ADOBase", "get_conductor", "scrConductor", {}, true, &cache.ado_base_get_conductor, cache.error);
    ok &= find_method_by_identity(cache.ado_base_klass, "ADOBase", "get_isScnGame", "System.Boolean", {}, true, &cache.ado_base_get_is_scn_game, cache.error);
    if (!ok) {
        if (cache.error.empty())
            cache.error = "core gameplay methods for overlay telemetry are unavailable";
        commit_failure();
        g_telemetry_cache_condition.notify_all();
        error = cache.error;
        return false;
    }

    const auto resolve_optional = [](
        void *klass,
        const char *type_name,
        const char *method_name,
        const char *return_type,
        std::initializer_list<const char *> parameter_types,
        bool is_static,
        void **target) {
        if (klass == nullptr)
            return;
        std::string optional_error;
        if (!find_method_by_identity(
                klass,
                type_name,
                method_name,
                return_type,
                parameter_types,
                is_static,
                target,
                optional_error)) {
            LOGI("optional telemetry member unavailable: %s.%s: %s",
                 type_name,
                 method_name,
                 optional_error.c_str());
        }
    };

    resolve_optional(cache.scr_controller_klass, "scrController", "get_planetarySystem", "PlanetarySystem", {}, false, &cache.scr_controller_get_planetary_system);
    resolve_optional(cache.scr_controller_klass, "scrController", "get_currFloor", "scrFloor", {}, false, &cache.scr_controller_get_curr_floor);
    resolve_optional(cache.scr_conductor_klass, "scrConductor", "get_songposition_minusi", "System.Double", {}, false, &cache.scr_conductor_get_songposition_minusi);
    resolve_optional(cache.rdc_klass, "RDC", "get_auto", "System.Boolean", {}, true, &cache.rdc_get_auto);
    resolve_optional(cache.ado_base_klass, "ADOBase", "get_isOfficialLevel", "System.Boolean", {}, true, &cache.ado_base_get_is_official_level);
    resolve_optional(cache.ado_base_klass, "ADOBase", "get_currentLevel", "System.String", {}, true, &cache.ado_base_get_current_level);
    resolve_optional(cache.ado_base_klass, "ADOBase", "get_levelPath", "System.String", {}, true, &cache.ado_base_get_level_path);
    resolve_optional(cache.component_klass, "UnityEngine.Component", "GetComponent", "UnityEngine.Component", {"System.Type"}, false, &cache.component_get_component);
    resolve_optional(cache.audio_source_klass, "UnityEngine.AudioSource", "get_time", "System.Single", {}, false, &cache.audio_source_get_time);
    resolve_optional(cache.audio_source_klass, "UnityEngine.AudioSource", "get_clip", "UnityEngine.AudioClip", {}, false, &cache.audio_source_get_clip);
    resolve_optional(cache.audio_source_klass, "UnityEngine.AudioSource", "get_pitch", "System.Single", {}, false, &cache.audio_source_get_pitch);
    resolve_optional(cache.audio_clip_klass, "UnityEngine.AudioClip", "get_length", "System.Single", {}, false, &cache.audio_clip_get_length);

    if (cache.time_klass != nullptr) {
        std::string time_error;
        bool time_ok = true;
        time_ok &= find_method_by_identity(
            cache.time_klass,
            "UnityEngine.Time",
            "get_time",
            "System.Single",
            {},
            true,
            &cache.time_get_time,
            time_error);
        time_ok &= find_method_by_identity(
            cache.time_klass,
            "UnityEngine.Time",
            "get_timeScale",
            "System.Single",
            {},
            true,
            &cache.time_get_time_scale,
            time_error);
        time_ok &= find_method_by_identity(
            cache.time_klass,
            "UnityEngine.Time",
            "get_frameCount",
            "System.Int32",
            {},
            true,
            &cache.time_get_frame_count,
            time_error);
        if (!time_ok) {
            cache.time_get_time = nullptr;
            cache.time_get_time_scale = nullptr;
            cache.time_get_frame_count = nullptr;
        }
    }

    cache.ready = true;
    cache.error.clear();
    {
        std::lock_guard<std::mutex> guard(g_telemetry_cache_lock);
        g_telemetry_cache = cache;
        g_telemetry_cache_building = false;
    }
    g_telemetry_cache_condition.notify_all();
    return true;
}

void resolve_target(RuntimeTarget &target) {
    target.resolve_attempted = true;
    target.resolved = false;
    target.resolve_error.clear();
    target.klass = nullptr;
    target.method = nullptr;
    target.function = nullptr;

    void *image = find_assembly_image(target.assembly_name);
    if (image == nullptr) {
        target.resolve_error = "assembly not found: " + target.assembly_name;
        return;
    }

    void *klass = find_class(image, target.namespace_name, target.type_name);
    if (klass == nullptr) {
        target.resolve_error = "class not found: " + target.type_name;
        return;
    }

    if (target.type_name == "scrMarginTracker" &&
        target.method_name == "CalculatePercentAcc" &&
        !resolve_margin_tracker_offsets(klass, target.resolve_error)) {
        return;
    }

    std::string method_error;
    ResolvedMethodMetadata method;
    if (!find_method(klass, target, method, method_error)) {
        target.resolve_error = method_error.empty()
            ? "method not found: " + target.type_name + "." + target.method_name
            : method_error;
        return;
    }

    if (method.function == nullptr) {
        target.resolve_error = "method has no runtime function pointer: " + target.type_name + "." + target.method_name;
        return;
    }

    target.klass = klass;
    target.method = const_cast<void *>(method.method_info);
    target.function = method.function;
    target.resolved = true;
}

int count_merged_slots(const RuleState &state) {
    return static_cast<int>(state.slots.size());
}

int count_resolved_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.state == SlotResolved || slot.state == SlotHookInstalled ||
               slot.state == SlotSkippedKnownConflict;
    }));
}

int count_failed_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.resolve_failed || slot.state == SlotInstallFailed || slot.state == SlotFaulted;
    }));
}

int count_pending_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.state == SlotPendingResolve;
    }));
}

int count_slot_rules(const RuleState &state) {
    int count = 0;
    for (const auto &slot : state.slots)
        count += slot_rule_count(slot);
    return count;
}

int count_enabled_slot_rules(const RuleState &state) {
    int count = 0;
    for (const auto &slot : state.slots)
        count += slot_enabled_rule_count(slot);
    return count;
}

int count_disabled_slot_rules(const RuleState &state) {
    return count_slot_rules(state) - count_enabled_slot_rules(state);
}

int count_installable_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.install_planned && !slot.install_blocked;
    }));
}

int count_install_blocked_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.install_blocked;
    }));
}

int count_installed_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.state == SlotHookInstalled;
    }));
}

int count_dispatcher_ready_slots(const RuleState &state) {
    return static_cast<int>(std::count_if(state.slots.begin(), state.slots.end(), [](const HookSlot &slot) {
        return slot.dispatcher_index >= 0;
    }));
}

int count_bound_dispatchers_locked() {
    int count = 0;
    for_each_dispatcher_runtime_slot([&](int, DispatcherRuntimeSlot &runtime) {
        if (runtime.permanently_bound)
            ++count;
    });
    return count;
}

int count_rules_for_mod_target_locked(const char *mod_id,
                                      const char *type_name,
                                      const char *method_name,
                                      int param_count) {
    if (mod_id == nullptr || type_name == nullptr || method_name == nullptr)
        return 0;

    int count = 0;
    for (const auto &bundle : g_state.bundles) {
        if (bundle.mod_id != mod_id)
            continue;
        for (const auto &target : bundle.targets) {
            if (target.type_name != type_name || target.method_name != method_name)
                continue;
            if (param_count >= 0 &&
                (!target.has_param_count || target.param_count != param_count)) {
                continue;
            }
            count += static_cast<int>(target.rules.size());
        }
    }
    return count;
}

struct FloorListView {
    void *items = nullptr;
    int32_t size = 0;
};

bool read_floor_list_view(const TelemetryRuntimeCache &cache,
                          void *level_maker,
                          FloorListView &view) {
    void *list = read_object_field(level_maker, cache.scr_level_maker_list_floors_offset);
    if (list == nullptr ||
        g_il2cpp_metadata.object_get_class == nullptr ||
        g_il2cpp_metadata.array_object_header_size == nullptr ||
        g_il2cpp_metadata.array_length == nullptr) {
        return false;
    }

    int32_t items_offset = -1;
    int32_t size_offset = -1;
    {
        std::lock_guard<std::mutex> guard(g_telemetry_cache_lock);
        items_offset = g_telemetry_cache.list_items_offset;
        size_offset = g_telemetry_cache.list_size_offset;
    }
    if (items_offset < 0 || size_offset < 0) {
        void *list_class = g_il2cpp_metadata.object_get_class(list);
        items_offset = find_field_offset(list_class, "_items", "items");
        size_offset = find_field_offset(list_class, "_size", "size");
        if (items_offset < 0 || size_offset < 0)
            return false;
        std::lock_guard<std::mutex> guard(g_telemetry_cache_lock);
        g_telemetry_cache.list_items_offset = items_offset;
        g_telemetry_cache.list_size_offset = size_offset;
    }

    if (!read_instance_value(list, size_offset, view.size) ||
        view.size < 0 ||
        view.size > 1000000) {
        return false;
    }
    view.items = read_object_field(list, items_offset);
    return view.size == 0 || view.items != nullptr;
}

void *floor_at(const FloorListView &view, int32_t index) {
    if (view.items == nullptr || index < 0 || index >= view.size)
        return nullptr;
    const uintptr_t runtime_length = g_il2cpp_metadata.array_length(view.items);
    if (static_cast<uintptr_t>(index) >= runtime_length)
        return nullptr;
    const uint32_t header_size = g_il2cpp_metadata.array_object_header_size();
    if (header_size == 0)
        return nullptr;
    const auto *address = static_cast<const char *>(view.items) +
        static_cast<uintptr_t>(header_size) +
        sizeof(void *) * static_cast<uintptr_t>(index);
    return *reinterpret_cast<void *const *>(address);
}

template <typename T>
bool read_static_value(void *field, T &value) {
    if (field == nullptr || g_il2cpp_metadata.field_static_get_value == nullptr)
        return false;
    g_il2cpp_metadata.field_static_get_value(field, &value);
    return true;
}

bool read_static_int32(void *field, int32_t &value) {
    return read_static_value(field, value);
}

bool capture_level_identity(const TelemetryRuntimeCache &cache) {
    const bool official = invoke_bool_noargs(cache.ado_base_get_is_official_level, false);
    std::string identity;
    if (official) {
        const std::string level = invoke_string_noargs(cache.ado_base_get_current_level);
        if (!level.empty())
            identity = "official:" + level;
    } else {
        const std::string path = invoke_string_noargs(cache.ado_base_get_level_path);
        if (!path.empty())
            identity = "path:" + path;
        else {
            const std::string level = invoke_string_noargs(cache.ado_base_get_current_level);
            if (!level.empty())
                identity = "level:" + level;
        }
    }

    if (identity.empty())
        return false;
    std::lock_guard<std::mutex> guard(g_timeline_state_lock);
    if (g_level_identity == identity)
        return false;
    g_level_identity = std::move(identity);
    return true;
}

int32_t current_checkpoint_index(int32_t current_seq_id) {
    std::lock_guard<std::mutex> guard(g_timeline_state_lock);
    return static_cast<int32_t>(std::upper_bound(
        g_checkpoint_seq_ids.begin(),
        g_checkpoint_seq_ids.end(),
        current_seq_id) - g_checkpoint_seq_ids.begin());
}

bool refresh_floor_metadata(const TelemetryRuntimeCache &cache,
                            const FloorListView &view,
                            int32_t current_seq_id) {
    std::vector<int32_t> checkpoints;
    checkpoints.reserve(16);
    double map_total_time = 0.0;

    for (int32_t index = 0; index < view.size; ++index) {
        void *floor = floor_at(view, index);
        if (floor == nullptr)
            continue;

        int32_t seq_id = index;
        read_instance_value(floor, cache.scr_floor_seq_id_offset, seq_id);
        if (index == view.size - 1)
            read_instance_value(floor, cache.scr_floor_entry_time_offset, map_total_time);

        if (cache.component_get_component != nullptr &&
            cache.ffx_checkpoint_type_object != nullptr) {
            void *args[] = {cache.ffx_checkpoint_type_object};
            if (invoke_il2cpp_method(cache.component_get_component, floor, args) != nullptr)
                checkpoints.push_back(seq_id);
        }
    }
    std::sort(checkpoints.begin(), checkpoints.end());

    int32_t start_seq_id = current_seq_id;
    {
        std::lock_guard<std::mutex> guard(g_timeline_state_lock);
        if (g_session_start_seq_valid)
            start_seq_id = g_session_start_seq_id;
        g_checkpoint_seq_ids = checkpoints;
        g_floor_metadata_initialized = true;
    }

    const float start_progress = view.size > 0
        ? std::clamp(static_cast<float>(start_seq_id) / static_cast<float>(view.size), 0.0f, 1.0f)
        : 0.0f;
    bool changed = false;
    changed |= atomic_store_if_changed(active_overlay_state().floor_count, view.size);
    changed |= atomic_store_if_changed(
        active_overlay_state().map_total_time_bits,
        float_to_bits(std::isfinite(map_total_time) && map_total_time > 0.0
            ? static_cast<float>(map_total_time)
            : 0.0f));
    changed |= atomic_store_if_changed(
        active_overlay_state().total_checkpoints,
        static_cast<int32_t>(checkpoints.size()));
    changed |= atomic_store_if_changed(
        active_overlay_state().current_checkpoint,
        current_checkpoint_index(current_seq_id));
    changed |= atomic_store_if_changed(
        active_overlay_state().start_progress_bits,
        float_to_bits(start_progress));
    return changed;
}

bool refresh_input_kps(int64_t now_ms) {
    return starray::realtime::refresh_kps(now_ms * 1'000'000LL);
}

bool publish_input_state(bool force) {
    starray::hud_logic::ensure_started();
    const auto producer = starray::realtime::read_input_snapshot();
    starray::hud_logic::CompletedInputSnapshot completed{};
    const bool completed_current =
        starray::hud_logic::read_latest_input_snapshot(completed) &&
        completed.source_generation >= producer.generation &&
        completed.source_sequence >= producer.latest_sequence;
    const uint32_t raw_generation = completed_current
        ? completed.source_generation
        : producer.generation;
    const uint32_t published_generation =
        active_owner_overlay_session().last_published_input_generation.load(
            std::memory_order_acquire);
    if (!force && raw_generation == published_generation)
        return false;

    bool changed = false;
    changed |= atomic_store_if_changed(
        active_overlay_state().input_state_generation,
        raw_generation);
    changed |= atomic_store_if_changed(
        active_overlay_state().input_held_mask,
        completed_current ? completed.held_mask : producer.held_mask);
    changed |= atomic_store_if_changed(
        active_overlay_state().input_last_down_mask,
        completed_current ? completed.last_down_mask : producer.last_down_mask);
    changed |= atomic_store_if_changed(
        active_overlay_state().input_last_up_mask,
        completed_current ? completed.last_up_mask : producer.last_up_mask);
    changed |= atomic_store_if_changed(
        active_overlay_state().input_total_count,
        completed_current ? completed.total_count : producer.total_count);
    changed |= atomic_store_if_changed(
        active_overlay_state().input_kps_bits,
        float_to_bits(completed_current ? completed.kps : producer.kps));
    active_owner_overlay_session().last_published_input_generation.store(
        raw_generation,
        std::memory_order_release);
    return changed;
}

bool poll_overlay_telemetry(void *controller, bool force) {
    std::string error;
    if (!ensure_telemetry_runtime_cache(error)) {
        static std::atomic<uint32_t> logged{0};
        if (logged.fetch_add(1, std::memory_order_relaxed) < 3)
            LOGI("overlay telemetry unavailable: %s", error.c_str());
        return false;
    }

    TelemetryRuntimeCache cache;
    {
        std::lock_guard<std::mutex> guard(g_telemetry_cache_lock);
        cache = g_telemetry_cache;
    }

    const int64_t now_ms = steady_time_ms();
    constexpr int64_t kTimelinePollIntervalMs = 100;
    const int64_t last_poll_ms =
        active_owner_overlay_session().last_timeline_poll_ms.load(
            std::memory_order_acquire);
    const bool timeline_due = force ||
        last_poll_ms == 0 ||
        now_ms - last_poll_ms >= kTimelinePollIntervalMs;

    bool changed = false;
    if (timeline_due) {
        active_owner_overlay_session().last_timeline_poll_ms.store(
            now_ms,
            std::memory_order_release);
        refresh_input_kps(now_ms);
    }
    changed |= publish_input_state(force || timeline_due);
    if (!timeline_due)
        return changed;

    void *controller_instance = invoke_object_noargs(cache.scr_controller_get_instance);
    void *ado_controller = invoke_object_noargs(cache.ado_base_get_controller);
    if (controller == nullptr)
        controller = controller_instance != nullptr ? controller_instance : ado_controller;
    void *conductor_instance = invoke_object_noargs(cache.scr_conductor_get_instance);
    void *ado_conductor = invoke_object_noargs(cache.ado_base_get_conductor);
    void *conductor = conductor_instance != nullptr ? conductor_instance : ado_conductor;
    void *level_maker = invoke_object_noargs(cache.scr_level_maker_get_instance);
    void *song = read_object_field(conductor, cache.scr_conductor_song_offset);
    void *planetary_system = controller == nullptr
        ? nullptr
        : invoke_object_noargs(
            cache.scr_controller_get_planetary_system,
            controller);
    void *current_floor = controller == nullptr
        ? nullptr
        : invoke_object_noargs(
            cache.scr_controller_get_curr_floor,
            controller);
    void *first_floor = read_object_field(
        controller,
        cache.scr_controller_first_floor_offset);
    changed |= atomic_store_if_changed(
        active_overlay_state().controller_pointer,
        reinterpret_cast<uintptr_t>(controller));
    changed |= atomic_store_if_changed(
        active_overlay_state().conductor_pointer,
        reinterpret_cast<uintptr_t>(conductor));
    changed |= atomic_store_if_changed(
        active_overlay_state().level_maker_pointer,
        reinterpret_cast<uintptr_t>(level_maker));
    changed |= atomic_store_if_changed(
        active_overlay_state().song_pointer,
        reinterpret_cast<uintptr_t>(song));
    changed |= atomic_store_if_changed(
        active_overlay_state().planetary_system_pointer,
        reinterpret_cast<uintptr_t>(planetary_system));
    changed |= atomic_store_if_changed(
        active_overlay_state().current_floor_pointer,
        reinterpret_cast<uintptr_t>(current_floor));
    changed |= atomic_store_if_changed(
        active_overlay_state().first_floor_pointer,
        reinterpret_cast<uintptr_t>(first_floor));
    uint8_t is_game_world = 0;
    read_instance_value(
        conductor,
        cache.scr_conductor_is_game_world_offset,
        is_game_world);
    const bool is_scn_game = invoke_bool_noargs(cache.ado_base_get_is_scn_game, false);
    const bool game_ready =
        controller != nullptr &&
        conductor != nullptr &&
        level_maker != nullptr &&
        is_game_world != 0;
    changed |= atomic_store_if_changed(
        active_overlay_state().is_scn_game,
        is_scn_game ? 1u : 0u);
    changed |= atomic_store_if_changed(
        active_overlay_state().game_ready,
        game_ready ? 1u : 0u);
    changed |= atomic_store_if_changed(
        active_overlay_state().is_game_world,
        is_game_world != 0 ? 1u : 0u);

    uint64_t valid_fields = kGameSnapshotState;
    if (active_overlay_state().accuracy_snapshot_count.load(std::memory_order_acquire) != 0)
        valid_fields |= kGameSnapshotAccuracy;
    if (active_overlay_state().bpm_snapshot_count.load(std::memory_order_acquire) != 0)
        valid_fields |= kGameSnapshotBpm;

    if (controller == nullptr || conductor == nullptr || level_maker == nullptr) {
        changed |= atomic_store_if_changed(
            active_overlay_state().valid_game_snapshot_fields,
            valid_fields);
        return changed;
    }

    FloorListView floors;
    if (!read_floor_list_view(cache, level_maker, floors)) {
        changed |= atomic_store_if_changed(
            active_overlay_state().valid_game_snapshot_fields,
            valid_fields);
        return changed;
    }

    int32_t current_seq_id = 0;
    int32_t current_state = 0;
    uint8_t no_fail = 0;
    read_instance_value(controller, cache.scr_controller_current_seq_id_offset, current_seq_id);
    read_instance_value(controller, cache.scr_controller_current_state_offset, current_state);
    read_instance_value(controller, cache.scr_controller_no_fail_offset, no_fail);
    if (current_floor == nullptr)
        current_floor = floor_at(floors, current_seq_id);
    if (first_floor == nullptr)
        first_floor = floor_at(floors, 0);
    changed |= atomic_store_if_changed(
        active_overlay_state().current_floor_pointer,
        reinterpret_cast<uintptr_t>(current_floor));
    changed |= atomic_store_if_changed(
        active_overlay_state().first_floor_pointer,
        reinterpret_cast<uintptr_t>(first_floor));
    const bool paused = invoke_bool_noargs_on(
        cache.scr_controller_get_paused,
        controller,
        false);

    bool metadata_initialized = false;
    {
        std::lock_guard<std::mutex> guard(g_timeline_state_lock);
        metadata_initialized = g_floor_metadata_initialized;
    }
    if (!metadata_initialized ||
        active_overlay_state().floor_count.load(std::memory_order_acquire) != floors.size) {
        changed |= refresh_floor_metadata(cache, floors, current_seq_id);
        capture_level_identity(cache);
    }

    float music_time = 0.0f;
    float music_total_time = 0.0f;
    float song_pitch = 1.0f;
    if (song != nullptr) {
        music_time = invoke_float_noargs(cache.audio_source_get_time, song, 0.0f);
        song_pitch = invoke_float_noargs(cache.audio_source_get_pitch, song, 1.0f);
        void *clip = invoke_object_noargs(cache.audio_source_get_clip, song);
        if (clip != nullptr)
            music_total_time = invoke_float_noargs(cache.audio_clip_get_length, clip, 0.0f);
    }
    if (!std::isfinite(music_time) || music_time < 0.0f)
        music_time = 0.0f;
    if (!std::isfinite(music_total_time) || music_total_time < 0.0f)
        music_total_time = 0.0f;
    if (!std::isfinite(song_pitch))
        song_pitch = 1.0f;
    {
        std::lock_guard<std::mutex> guard(g_timeline_state_lock);
        if (music_time > 0.0f)
            g_music_has_played = true;
        else if (g_music_has_played && music_total_time > 0.0f)
            music_time = music_total_time;
    }
    if (music_total_time > 0.0f)
        music_time = std::min(music_time, music_total_time);

    double add_offset = 0.0;
    read_instance_value(conductor, cache.scr_conductor_add_offset_offset, add_offset);
    const double song_position = invoke_double_noargs(
        cache.scr_conductor_get_songposition_minusi,
        conductor,
        0.0);
    if (!std::isfinite(add_offset))
        add_offset = 0.0;
    const double finite_song_position = std::isfinite(song_position)
        ? song_position
        : 0.0;
    const float map_total_time = bits_to_float(
        active_overlay_state().map_total_time_bits.load(std::memory_order_acquire));
    float map_time = current_state == 1
        ? 0.0f
        : static_cast<float>(add_offset + finite_song_position);
    if (!std::isfinite(map_time))
        map_time = 0.0f;
    map_time = std::clamp(map_time, 0.0f, std::max(0.0f, map_total_time));

    starray::hud_logic::ClockAnchorSnapshot clock_anchor{};
    clock_anchor.session_generation =
        starray::realtime::read_input_snapshot().session_generation;
    clock_anchor.monotonic_raw_ns = starray::realtime::monotonic_now_ns();
    if (cache.time_get_time != nullptr &&
        cache.time_get_time_scale != nullptr &&
        cache.time_get_frame_count != nullptr) {
        const float unity_time = invoke_float_noargs(cache.time_get_time, nullptr, 0.0f);
        const float unity_time_scale = invoke_float_noargs(
            cache.time_get_time_scale,
            nullptr,
            1.0f);
        if (std::isfinite(unity_time) && std::isfinite(unity_time_scale)) {
            clock_anchor.unity_scaled_seconds = unity_time;
            clock_anchor.unity_time_scale = unity_time_scale;
            clock_anchor.frame_count = invoke_int32_noargs(
                cache.time_get_frame_count,
                nullptr,
                0);
            clock_anchor.valid_mask |=
                starray::hud_logic::ClockAnchorUnityScaled |
                starray::hud_logic::ClockAnchorFrameCount;
        }
    }
    if (std::isfinite(song_position)) {
        clock_anchor.song_position_seconds = finite_song_position;
        clock_anchor.valid_mask |= starray::hud_logic::ClockAnchorSongPosition;
    }
    if (song != nullptr && std::isfinite(music_time)) {
        clock_anchor.audio_position_seconds = music_time;
        clock_anchor.valid_mask |= starray::hud_logic::ClockAnchorAudioPosition;
    }
    if (std::isfinite(map_time)) {
        clock_anchor.map_position_seconds = map_time;
        clock_anchor.valid_mask |= starray::hud_logic::ClockAnchorMapPosition;
    }
    starray::hud_logic::publish_clock_anchor(clock_anchor);

    int32_t checkpoints_used = 0;
    read_static_int32(cache.scr_controller_checkpoints_used_field, checkpoints_used);
    const int32_t checkpoint_index = current_checkpoint_index(current_seq_id);
    const float progress = floors.size > 0
        ? std::clamp(static_cast<float>(current_seq_id + 1) / static_cast<float>(floors.size), 0.0f, 1.0f)
        : 0.0f;

    bool timeline_changed = false;
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().music_time_bits,
        float_to_bits(music_time));
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().music_total_time_bits,
        float_to_bits(music_total_time));
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().map_time_bits,
        float_to_bits(map_time));
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().checkpoints_used,
        checkpoints_used);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().current_checkpoint,
        checkpoint_index);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().current_seq_id,
        current_seq_id);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().progress_bits,
        float_to_bits(progress));

    const bool rdc_auto = invoke_bool_noargs(cache.rdc_get_auto, false);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().rdc_auto,
        rdc_auto ? 1u : 0u);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().no_fail,
        no_fail != 0 ? 1u : 0u);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().paused,
        paused ? 1u : 0u);
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().song_pitch_bits,
        float_to_bits(song_pitch));
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().conductor_add_offset_bits,
        double_to_bits(add_offset));
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().conductor_songposition_minusi_bits,
        double_to_bits(finite_song_position));

    const bool session_auto = rdc_auto || no_fail != 0;
    if (session_auto)
        timeline_changed |= atomic_store_if_changed(active_overlay_state().session_auto, 1u);

    double planet_speed = 1.0;
    read_instance_value(
        planetary_system,
        cache.planetary_system_speed_offset,
        planet_speed);
    if (!std::isfinite(planet_speed) || planet_speed <= 0.0)
        planet_speed = 1.0;
    float planet_speed_value = static_cast<float>(planet_speed);
    if (!std::isfinite(planet_speed_value) || planet_speed_value <= 0.0f)
        planet_speed_value = 1.0f;
    // Planet speed is live level state; variable-speed events must reach TBPM.
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().planet_speed_bits,
        float_to_bits(planet_speed_value));

    const bool initialize_speed =
        active_overlay_state().speed_multiplier_bits.load(std::memory_order_acquire) == 0;
    if (initialize_speed) {
        float multiplier = song_pitch * planet_speed_value;
        if (!std::isfinite(multiplier) || multiplier <= 0.0f)
            multiplier = 1.0f;
        timeline_changed |= atomic_store_if_changed(
            active_overlay_state().speed_multiplier_bits,
            float_to_bits(multiplier));
    }

    if (timeline_changed)
        active_overlay_state().timeline_snapshot_count.fetch_add(1, std::memory_order_relaxed);
    valid_fields |= kGameSnapshotProgress |
        kGameSnapshotCurrentSeqId |
        kGameSnapshotCheckpoints |
        kGameSnapshotFloor |
        kGameSnapshotTimeline |
        kGameSnapshotState |
        kGameSnapshotConductor |
        kGameSnapshotSongPitch |
        kGameSnapshotPlayer;
    if (planetary_system != nullptr && cache.planetary_system_speed_offset >= 0)
        valid_fields |= kGameSnapshotPlanetSpeed;
    timeline_changed |= atomic_store_if_changed(
        active_overlay_state().valid_game_snapshot_fields,
        valid_fields);
    return changed || timeline_changed;
}

void disable_dispatcher_runtime_slot(int index) {
    auto *runtime = dispatcher_runtime_slot(index);
    if (runtime == nullptr)
        return;

    runtime->enabled.store(0, std::memory_order_release);
    runtime->before_op_mask.store(0, std::memory_order_release);
    runtime->after_op_mask.store(0, std::memory_order_release);
    std::atomic_store_explicit(
        &runtime->managed_event_after_rules,
        std::shared_ptr<const ManagedEventDispatchSnapshot>{},
        std::memory_order_release);
    std::atomic_store_explicit(
        &runtime->managed_prefix_before_rules,
        std::shared_ptr<const ManagedPrefixDispatchSnapshot>{},
        std::memory_order_release);
    std::atomic_store_explicit(
        &runtime->owner_overlay_after_rules,
        std::shared_ptr<const OwnerOverlayDispatchSnapshot>{},
        std::memory_order_release);
    std::atomic_store_explicit(
        &runtime->session_rule_masks,
        std::shared_ptr<const SessionRuleMaskSnapshot>{},
        std::memory_order_release);
    runtime->slot_id.store(0, std::memory_order_release);
    runtime->target_kind.store(kTargetKindUnknown, std::memory_order_release);
    runtime->call_count.store(0, std::memory_order_release);
    runtime->fault_count.store(0, std::memory_order_release);
}

void reset_owner_overlay_session_metrics() {
    active_owner_overlay_session().last_margin_callback_ms.store(
        0,
        std::memory_order_release);
    active_overlay_state().judgement_hit_count.store(0, std::memory_order_release);
    active_overlay_state().judgement_reset_count.store(0, std::memory_order_release);
    active_overlay_state().last_hit_margin.store(0, std::memory_order_release);
    active_overlay_state().floor_move_count.store(0, std::memory_order_release);
    active_overlay_state().last_floor_exit_angle_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().last_floor_move_hit_margin.store(0, std::memory_order_release);
    active_overlay_state().player_hit_count.store(0, std::memory_order_release);
    active_overlay_state().last_player_hit_is_auto.store(0, std::memory_order_release);
    active_overlay_state().death_count.store(0, std::memory_order_release);
    active_overlay_state().last_death_overload.store(0, std::memory_order_release);
    active_overlay_state().last_death_multipress.store(0, std::memory_order_release);
    active_overlay_state().last_death_hitbox.store(0, std::memory_order_release);
    active_overlay_state().hit_timing_count.store(0, std::memory_order_release);
    active_overlay_state().last_hit_timing_ms_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().last_hit_timing_margin.store(0, std::memory_order_release);
    active_overlay_state().accuracy_snapshot_count.store(0, std::memory_order_release);
    active_overlay_state().percent_acc_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().percent_x_acc_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().progress_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().combo_count.store(0, std::memory_order_release);
    active_overlay_state().bpm_snapshot_count.store(0, std::memory_order_release);
    active_overlay_state().tile_bpm_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().kps_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().timeline_snapshot_count.store(0, std::memory_order_release);
    active_overlay_state().music_time_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().music_total_time_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().map_time_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().map_total_time_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().checkpoints_used.store(0, std::memory_order_release);
    active_overlay_state().current_checkpoint.store(0, std::memory_order_release);
    active_overlay_state().total_checkpoints.store(0, std::memory_order_release);
    active_overlay_state().current_seq_id.store(0, std::memory_order_release);
    active_overlay_state().floor_count.store(0, std::memory_order_release);
    active_overlay_state().start_progress_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().speed_multiplier_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().planet_speed_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().session_auto.store(0, std::memory_order_release);
    active_overlay_state().rdc_auto.store(0, std::memory_order_release);
    active_overlay_state().no_fail.store(0, std::memory_order_release);
    active_overlay_state().paused.store(0, std::memory_order_release);
    active_overlay_state().is_game_world.store(0, std::memory_order_release);
    active_overlay_state().song_pitch_bits.store(float_to_bits(1.0f), std::memory_order_release);
    active_overlay_state().conductor_add_offset_bits.store(double_to_bits(0.0), std::memory_order_release);
    active_overlay_state().conductor_songposition_minusi_bits.store(double_to_bits(0.0), std::memory_order_release);
    active_overlay_state().is_scn_game.store(0, std::memory_order_release);
    active_overlay_state().game_ready.store(0, std::memory_order_release);
    active_overlay_state().input_state_generation.store(0, std::memory_order_release);
    active_overlay_state().input_held_mask.store(0, std::memory_order_release);
    active_overlay_state().input_last_down_mask.store(0, std::memory_order_release);
    active_overlay_state().input_last_up_mask.store(0, std::memory_order_release);
    active_overlay_state().input_total_count.store(0, std::memory_order_release);
    active_overlay_state().input_kps_bits.store(float_to_bits(0.0f), std::memory_order_release);
    active_overlay_state().valid_game_snapshot_fields.store(0, std::memory_order_release);
    active_overlay_state().controller_pointer.store(0, std::memory_order_release);
    active_overlay_state().conductor_pointer.store(0, std::memory_order_release);
    active_overlay_state().level_maker_pointer.store(0, std::memory_order_release);
    active_overlay_state().current_floor_pointer.store(0, std::memory_order_release);
    active_overlay_state().first_floor_pointer.store(0, std::memory_order_release);
    active_overlay_state().song_pointer.store(0, std::memory_order_release);
    active_overlay_state().planetary_system_pointer.store(0, std::memory_order_release);
    active_owner_overlay_session().last_timeline_poll_ms.store(
        0,
        std::memory_order_release);
    active_owner_overlay_session().last_published_input_generation.store(
        0,
        std::memory_order_release);
}

void reset_shared_overlay_session_facts() {
    {
        OwnerOverlayScope shared_scope(&g_legacy_overlay_session);
        auto &shared = active_overlay_state();
        shared.session_epoch.fetch_add(1, std::memory_order_acq_rel);
        shared.visible.store(0, std::memory_order_release);
        shared.practice.store(0, std::memory_order_release);
        shared.show_count.store(0, std::memory_order_release);
        shared.hide_count.store(0, std::memory_order_release);
        shared.player_update_count.store(0, std::memory_order_release);
        shared.state_change_count.store(0, std::memory_order_release);
        shared.last_op.store(0xFFFFFFFFu, std::memory_order_release);
        shared.last_target_kind.store(kTargetKindUnknown, std::memory_order_release);
        shared.player_count.store(0, std::memory_order_release);
        shared.last_seq_id.store(0, std::memory_order_release);
        shared.last_is_restart.store(0, std::memory_order_release);
        shared.last_wipe_direction.store(0, std::memory_order_release);
        shared.last_reset_to_editor.store(0, std::memory_order_release);
        shared.attempt_count.store(0, std::memory_order_release);
        reset_owner_overlay_session_metrics();
        shared.generation.fetch_add(1, std::memory_order_release);
    }
    clear_hit_margin_snapshot();
    std::lock_guard<std::mutex> guard(g_timeline_state_lock);
    g_checkpoint_seq_ids.clear();
    g_level_identity.clear();
    g_session_start_seq_id = 0;
    g_session_start_seq_valid = false;
    g_floor_metadata_initialized = false;
    g_music_has_played = false;
}

void reset_overlay_runtime_state() {
    active_overlay_state().visible.store(0, std::memory_order_release);
    active_overlay_state().practice.store(0, std::memory_order_release);
    active_overlay_state().show_count.store(0, std::memory_order_release);
    active_overlay_state().hide_count.store(0, std::memory_order_release);
    active_overlay_state().player_update_count.store(0, std::memory_order_release);
    active_overlay_state().state_change_count.store(0, std::memory_order_release);
    active_overlay_state().last_op.store(0xFFFFFFFFu, std::memory_order_release);
    active_overlay_state().last_target_kind.store(kTargetKindUnknown, std::memory_order_release);
    active_overlay_state().player_count.store(0, std::memory_order_release);
    active_overlay_state().last_seq_id.store(0, std::memory_order_release);
    active_overlay_state().last_is_restart.store(0, std::memory_order_release);
    active_overlay_state().last_wipe_direction.store(0, std::memory_order_release);
    active_overlay_state().last_reset_to_editor.store(0, std::memory_order_release);
    active_overlay_state().attempt_count.store(0, std::memory_order_release);
    reset_owner_overlay_session_metrics();
    reset_shared_overlay_session_facts();
    active_overlay_state().generation.fetch_add(1, std::memory_order_release);
}

uint32_t target_kind_for_slot(const HookSlot &slot) {
    if (slot.type_name == "scnGame" && slot.method_name == "Play")
        return kTargetKindScnGamePlay;
    if (slot.type_name == "scrPressToStart" && slot.method_name == "ShowText")
        return kTargetKindPressToStartShowText;
    if (slot.type_name == "MonsterLove.StateMachine.StateBehaviour" && slot.method_name == "ChangeState")
        return kTargetKindStateBehaviourChangeState;
    if (slot.type_name == "scrUIController" && slot.method_name == "WipeToBlack")
        return kTargetKindUiControllerWipeToBlack;
    if (slot.type_name == "scnEditor" && slot.method_name == "ResetScene")
        return kTargetKindEditorResetScene;
    if (slot.type_name == "scnEditor" && slot.method_name == "SwitchToEditMode")
        return kTargetKindEditorSwitchToEditMode;
    if (slot.type_name == "scrController" && slot.method_name == "StartLoadingScene")
        return kTargetKindControllerStartLoadingScene;
    if (slot.type_name == "scrMistakesManager" && slot.method_name == "SetPlayerCount")
        return kTargetKindMistakesSetPlayerCount;
    if (slot.type_name == "scrMarginTracker" && slot.method_name == "AddHit")
        return kTargetKindMarginTrackerAddHit;
    if (slot.type_name == "scrMarginTracker" && slot.method_name == "Reset")
        return kTargetKindMarginTrackerReset;
    if (slot.type_name == "scrMarginTracker" && slot.method_name == "CalculatePercentAcc")
        return kTargetKindMarginTrackerCalculatePercentAcc;
    if (slot.type_name == "scrPlanet" && slot.method_name == "MoveToNextFloor")
        return kTargetKindPlanetMoveToNextFloor;
    if (slot.type_name == "scrPlayer" && slot.method_name == "Hit")
        return kTargetKindPlayerHit;
    if (slot.type_name == "scrPlayer" && slot.method_name == "Die")
        return kTargetKindPlayerDie;
    if (slot.type_name == "scrMisc" && slot.method_name == "GetHitMargin")
        return kTargetKindMiscGetHitMargin;
    if (slot.type_name == "scrController" && slot.method_name == "QuitToMainMenu")
        return kTargetKindControllerQuitToMainMenu;
    if (slot.type_name == "scnEditor" && slot.method_name == "OttoUpdate")
        return kTargetKindScnEditorOttoUpdate;
    if (slot.type_name == "scrFloor" && slot.method_name == "Start")
        return kTargetKindScrFloorStart;
    if (slot.type_name == "scrPlanet" && slot.method_name == "Start")
        return kTargetKindScrPlanetStart;
    if (slot.type_name == "scrLogoText" && slot.method_name == "Awake")
        return kTargetKindScrLogoTextAwake;
    if (slot.type_name == "PlanetarySystem" && slot.method_name == "RainbowMode")
        return kTargetKindPlanetarySystemRainbowMode;
    if (slot.type_name == "PlanetarySystem" && slot.method_name == "EnbyMode")
        return kTargetKindPlanetarySystemEnbyMode;
    if (slot.type_name == "scrLogoText" && slot.method_name == "UpdateColors")
        return kTargetKindScrLogoTextUpdateColors;
    if (slot.type_name == "scrLogoText" && slot.method_name == "LateUpdate")
        return kTargetKindScrLogoTextLateUpdate;
    if (slot.type_name == "scrFloor" && slot.method_name == "SetTileColor")
        return kTargetKindScrFloorSetTileColor;
    if (slot.type_name == "PlanetRenderer" && slot.method_name == "LoadPlanetColor")
        return kTargetKindPlanetRendererLoadPlanetColor;
    if (slot.type_name == "PlanetRenderer" && slot.method_name == "SetRainbow")
        return kTargetKindPlanetRendererSetRainbow;
    if (slot.type_name == "PlanetRenderer" && slot.method_name == "SetColor")
        return kTargetKindPlanetRendererSetColor;
    if (slot.type_name == "PlanetRenderer" &&
        (slot.method_name == "SetPlanetColor" ||
         slot.method_name == "SetCoreColor" ||
         slot.method_name == "SetTailColor" ||
         slot.method_name == "SetRingColor" ||
         slot.method_name == "SetFaceColor")) {
        return kTargetKindPlanetRendererSetColorArg;
    }
    if (slot.type_name == "scrController" && slot.method_name == "PlayerControl_Update")
        return kTargetKindControllerPlayerControlUpdate;
    if (slot.type_name == "scrPlayer" && slot.method_name == "HitInputEvent")
        return kTargetKindPlayerHitInputEvent;

    return kTargetKindUnknown;
}

ManagedPrefixDispatchSnapshot build_managed_prefix_dispatch_snapshot(
    const std::vector<HookSlotRuleRef> &rules) {
    std::vector<const HookSlotRuleRef *> nodes;
    nodes.reserve(rules.size());
    for (const auto &rule : rules) {
        if (rule.enabled &&
            (rule.op_code == kRuleOpManagedSynchronousPrefix ||
             rule.op_code == kRuleOpManagedRenderCallback) &&
            rule.managed_prefix_id != 0) {
            nodes.push_back(&rule);
        }
    }
    const size_t count = nodes.size();
    std::vector<std::set<size_t>> outgoing(count);
    std::vector<size_t> indegree(count, 0);
    const auto has_owner = [](const std::vector<std::string> &owners,
                              const std::string &owner) {
        return !owner.empty() &&
               std::find(owners.begin(), owners.end(), owner) != owners.end();
    };
    const auto add_edge = [&](size_t from, size_t to) {
        if (from == to)
            return;
        if (outgoing[from].insert(to).second)
            ++indegree[to];
    };

    for (size_t left = 0; left < count; ++left) {
        for (size_t right = 0; right < count; ++right) {
            if (left == right)
                continue;
            if (has_owner(nodes[left]->managed_prefix_before,
                          nodes[right]->managed_prefix_owner)) {
                add_edge(left, right);
            }
            if (has_owner(nodes[left]->managed_prefix_after,
                          nodes[right]->managed_prefix_owner)) {
                add_edge(right, left);
            }
        }
    }

    const auto comes_first = [&](size_t left, size_t right) {
        const auto *a = nodes[left];
        const auto *b = nodes[right];
        if (a->managed_prefix_priority != b->managed_prefix_priority)
            return a->managed_prefix_priority > b->managed_prefix_priority;
        const uint64_t a_index = a->managed_prefix_registration_index == 0
            ? std::numeric_limits<uint64_t>::max()
            : a->managed_prefix_registration_index;
        const uint64_t b_index = b->managed_prefix_registration_index == 0
            ? std::numeric_limits<uint64_t>::max()
            : b->managed_prefix_registration_index;
        if (a_index != b_index)
            return a_index < b_index;
        if (a->bundle_id != b->bundle_id)
            return a->bundle_id < b->bundle_id;
        return a->managed_prefix_id < b->managed_prefix_id;
    };

    ManagedPrefixDispatchSnapshot snapshot;
    snapshot.reserve(count);
    std::vector<bool> emitted(count, false);
    for (size_t emitted_count = 0; emitted_count < count; ++emitted_count) {
        size_t selected = count;
        for (size_t index = 0; index < count; ++index) {
            if (emitted[index] || indegree[index] != 0)
                continue;
            if (selected == count || comes_first(index, selected))
                selected = index;
        }

        // Ordering cycles must not disable the physical hook. Break the cycle with the same
        // deterministic priority/registration key used for independent patches.
        if (selected == count) {
            for (size_t index = 0; index < count; ++index) {
                if (emitted[index])
                    continue;
                if (selected == count || comes_first(index, selected))
                    selected = index;
            }
        }

        emitted[selected] = true;
        const auto ring = g_managed_event_rings.find(nodes[selected]->bundle_id);
        if (ring != g_managed_event_rings.end() && ring->second != nullptr) {
            snapshot.push_back(ManagedPrefixDispatchTarget{
                .bundle_id = nodes[selected]->bundle_id,
                .managed_prefix_id = nodes[selected]->managed_prefix_id,
                .ring = ring->second,
                .pc_mod_session = nodes[selected]->pc_mod_session,
                .owner_filtered =
                    nodes[selected]->op_code == kRuleOpManagedRenderCallback,
            });
        }
        for (const auto next : outgoing[selected]) {
            if (!emitted[next] && indegree[next] != 0)
                --indegree[next];
        }
    }
    return snapshot;
}

// Harmony uses the same PatchSorter for Postfix callbacks as it does for Prefix
// callbacks: priority descending, registration index ascending, then the declared
// before/after owner graph. This function runs only while rebuilding an immutable
// dispatcher snapshot; the hook and managed-frame paths only read that snapshot.
ManagedEventDispatchSnapshot build_managed_event_dispatch_snapshot(
    const std::vector<HookSlotRuleRef> &rules,
    uint32_t target_kind) {
    std::vector<const HookSlotRuleRef *> nodes;
    nodes.reserve(rules.size());
    for (const auto &rule : rules) {
        if (!rule.enabled ||
            rule.op_code != kRuleOpManagedEventCallback ||
            rule.managed_event_id == 0)
            continue;
        const auto ring = g_managed_event_rings.find(rule.bundle_id);
        if (ring != g_managed_event_rings.end() && ring->second != nullptr)
            nodes.push_back(&rule);
    }

    const size_t count = nodes.size();
    std::vector<std::set<size_t>> outgoing(count);
    std::vector<size_t> indegree(count, 0);
    const auto has_owner = [](const std::vector<std::string> &owners,
                              const std::string &owner) {
        return !owner.empty() &&
               std::find(owners.begin(), owners.end(), owner) != owners.end();
    };
    const auto add_edge = [&](size_t from, size_t to) {
        if (from == to)
            return;
        if (outgoing[from].insert(to).second)
            ++indegree[to];
    };

    for (size_t left = 0; left < count; ++left) {
        for (size_t right = 0; right < count; ++right) {
            if (left == right)
                continue;
            if (has_owner(nodes[left]->managed_event_before,
                          nodes[right]->managed_event_owner)) {
                add_edge(left, right);
            }
            if (has_owner(nodes[left]->managed_event_after,
                          nodes[right]->managed_event_owner)) {
                add_edge(right, left);
            }
        }
    }

    const auto comes_first = [&](size_t left, size_t right) {
        const auto *a = nodes[left];
        const auto *b = nodes[right];
        if (a->managed_event_priority != b->managed_event_priority)
            return a->managed_event_priority > b->managed_event_priority;
        const uint64_t a_index = a->managed_event_registration_index == 0
            ? std::numeric_limits<uint64_t>::max()
            : a->managed_event_registration_index;
        const uint64_t b_index = b->managed_event_registration_index == 0
            ? std::numeric_limits<uint64_t>::max()
            : b->managed_event_registration_index;
        if (a_index != b_index)
            return a_index < b_index;
        if (a->bundle_id != b->bundle_id)
            return a->bundle_id < b->bundle_id;
        return a->rule_id < b->rule_id;
    };

    ManagedEventDispatchSnapshot snapshot;
    snapshot.reserve(count);
    std::vector<bool> emitted(count, false);
    for (size_t emitted_count = 0; emitted_count < count; ++emitted_count) {
        size_t selected = count;
        for (size_t index = 0; index < count; ++index) {
            if (emitted[index] || indegree[index] != 0)
                continue;
            if (selected == count || comes_first(index, selected))
                selected = index;
        }

        // An owner cycle must not disable the physical hook. Match Harmony's
        // deterministic cycle break using the same priority/index key.
        if (selected == count) {
            for (size_t index = 0; index < count; ++index) {
                if (emitted[index])
                    continue;
                if (selected == count || comes_first(index, selected))
                    selected = index;
            }
        }

        emitted[selected] = true;
        const auto *rule = nodes[selected];
        const auto ring = g_managed_event_rings.find(rule->bundle_id);
        if (ring != g_managed_event_rings.end() && ring->second != nullptr) {
            snapshot.push_back(ManagedEventDispatchTarget{
                .ring = ring->second,
                .pc_mod_session = rule->pc_mod_session,
                .managed_event_id = rule->managed_event_id,
                .lifecycle_boundary = is_managed_event_lifecycle_boundary(target_kind),
            });
        }
        for (const auto next : outgoing[selected]) {
            if (!emitted[next] && indegree[next] != 0)
                --indegree[next];
        }
    }
    return snapshot;
}

OwnerOverlayDispatchSnapshot build_owner_overlay_dispatch_snapshot(
    const std::vector<HookSlotRuleRef> &rules) {
    struct Builder {
        std::string mod_id;
        PcModSessionToken pc_mod_session;
        uint64_t mask = 0;
        std::shared_ptr<OwnerOverlaySession> session;
    };
    std::vector<Builder> builders;
    for (const auto &rule : rules) {
        if (!rule.enabled || rule.op_code < 0 || rule.op_code >= 63)
            continue;
        const uint64_t op_mask = 1ULL << static_cast<uint32_t>(rule.op_code);
        if ((op_mask & kOwnerOverlayOpMask) == 0)
            continue;
        const auto bundle = std::find_if(
            g_state.bundles.begin(),
            g_state.bundles.end(),
            [&rule](const RuntimeBundle &candidate) {
                return candidate.bundle_id == rule.bundle_id;
            });
        if (bundle == g_state.bundles.end() || bundle->mod_id.empty())
            continue;
        auto builder = std::find_if(
            builders.begin(), builders.end(),
            [&bundle](const Builder &candidate) {
                return candidate.mod_id == bundle->mod_id &&
                       candidate.pc_mod_session == bundle->pc_mod_session;
            });
        if (builder == builders.end()) {
            builders.push_back(Builder{
                .mod_id = bundle->mod_id,
                .pc_mod_session = bundle->pc_mod_session,
                .session = get_or_create_owner_overlay_session(bundle->mod_id),
            });
            builder = std::prev(builders.end());
        }
        builder->mask |= op_mask;
    }

    OwnerOverlayDispatchSnapshot snapshot;
    snapshot.reserve(builders.size());
    for (auto &builder : builders) {
        if (builder.mask == 0 || builder.session == nullptr)
            continue;
        OwnerOverlayDispatchTarget target{
            .session = std::move(builder.session),
            .pc_mod_session = builder.pc_mod_session,
            .after_op_mask = builder.mask,
        };
        for (const auto &bundle : g_state.bundles) {
            if (bundle.mod_id == builder.mod_id &&
                bundle.pc_mod_session == builder.pc_mod_session) {
                target.bundle_ids.push_back(bundle.bundle_id);
            }
        }
        snapshot.push_back(std::move(target));
    }
    return snapshot;
}

void publish_managed_dispatch_snapshots_locked(const HookSlot &slot, bool enabled) {
    auto *runtime = dispatcher_runtime_slot(slot.dispatcher_index);
    if (runtime == nullptr)
        return;

    auto event_snapshot = std::make_shared<ManagedEventDispatchSnapshot>();
    auto prefix_snapshot = std::make_shared<ManagedPrefixDispatchSnapshot>();
    auto overlay_snapshot = std::make_shared<OwnerOverlayDispatchSnapshot>();
    auto session_mask_snapshot = std::make_shared<SessionRuleMaskSnapshot>();
    if (enabled) {
        *overlay_snapshot = build_owner_overlay_dispatch_snapshot(slot.after_rules);
        std::lock_guard<std::mutex> managed_events_guard(g_managed_events_lock);
        *event_snapshot = build_managed_event_dispatch_snapshot(
            slot.after_rules,
            target_kind_for_slot(slot));
        *prefix_snapshot = build_managed_prefix_dispatch_snapshot(slot.before_rules);
        const auto append_session_masks = [&session_mask_snapshot](
            const std::vector<HookSlotRuleRef> &rules,
            bool before) {
            for (const auto &rule : rules) {
                if (!rule.enabled || rule.op_code < 0 || rule.op_code >= 63 ||
                    rule.pc_mod_session.session_handle == 0) {
                    continue;
                }
                auto binding = std::find_if(
                    session_mask_snapshot->begin(), session_mask_snapshot->end(),
                    [&rule](const SessionRuleMask &candidate) {
                        return candidate.pc_mod_session == rule.pc_mod_session;
                    });
                if (binding == session_mask_snapshot->end()) {
                    session_mask_snapshot->push_back(SessionRuleMask{
                        .pc_mod_session = rule.pc_mod_session,
                    });
                    binding = std::prev(session_mask_snapshot->end());
                }
                const uint64_t op_mask = 1ULL << static_cast<uint32_t>(rule.op_code);
                if (before)
                    binding->before_op_mask |= op_mask;
                else
                    binding->after_op_mask |= op_mask;
            }
        };
        append_session_masks(slot.before_rules, true);
        append_session_masks(slot.after_rules, false);
    }
    std::atomic_store_explicit(
        &runtime->managed_event_after_rules,
        std::shared_ptr<const ManagedEventDispatchSnapshot>(std::move(event_snapshot)),
        std::memory_order_release);

    std::atomic_store_explicit(
        &runtime->managed_prefix_before_rules,
        std::shared_ptr<const ManagedPrefixDispatchSnapshot>(std::move(prefix_snapshot)),
        std::memory_order_release);
    std::atomic_store_explicit(
        &runtime->owner_overlay_after_rules,
        std::shared_ptr<const OwnerOverlayDispatchSnapshot>(std::move(overlay_snapshot)),
        std::memory_order_release);
    std::atomic_store_explicit(
        &runtime->session_rule_masks,
        std::shared_ptr<const SessionRuleMaskSnapshot>(
            std::move(session_mask_snapshot)),
        std::memory_order_release);
}

void synchronize_bound_dispatchers_locked() {
    std::vector<bool> active(static_cast<size_t>(g_dispatcher_capacity.load(std::memory_order_acquire)), false);
    for (auto &slot : g_state.slots) {
        auto *runtime = dispatcher_runtime_slot(slot.dispatcher_index);
        if (slot.state != SlotHookInstalled || runtime == nullptr) {
            continue;
        }

        active[static_cast<size_t>(slot.dispatcher_index)] = true;
        std::string disabled_reason;
        if (!runtime->permanently_bound || runtime->bound_key != slot.key)
            disabled_reason = "dispatcher binding does not match slot key";
        else if (runtime->bound_abi_kind != slot.abi_kind)
            disabled_reason = "dispatcher binding ABI does not match slot ABI";
        else if (slot.function != nullptr && runtime->bound_function != slot.function)
            disabled_reason = "dispatcher binding function does not match resolved target";
        else if (slot_enabled_rule_count(slot) <= 0)
            disabled_reason = "all rules disabled";
        else if (enabled_rule_count(slot.replace_rules) > 0)
            disabled_reason = "replace rules are unsupported by installed fixed dispatcher";
        else if (!is_first_dispatcher_abi_supported(slot))
            disabled_reason = "installed fixed dispatcher ABI is unsupported";
        else {
            std::string unsupported_reason;
            if (has_unsupported_first_dispatcher_op(slot, unsupported_reason))
                disabled_reason = unsupported_reason;
        }

        const uint64_t before_mask = disabled_reason.empty() ? build_before_op_mask(slot) : 0;
        const uint64_t after_mask = disabled_reason.empty() ? build_after_op_mask(slot) : 0;
        runtime->slot_id.store(slot.slot_id, std::memory_order_release);
        runtime->target_kind.store(target_kind_for_slot(slot), std::memory_order_release);
        runtime->before_op_mask.store(before_mask, std::memory_order_release);
        runtime->after_op_mask.store(after_mask, std::memory_order_release);
        const bool enable = (before_mask != 0 || after_mask != 0) &&
                            runtime->original.load(std::memory_order_acquire) != nullptr;
        runtime->enabled.store(enable ? 1u : 0u, std::memory_order_release);
        publish_managed_dispatch_snapshots_locked(slot, enable);
        slot.status = enable
            ? "hook installed; fixed ops active"
            : "hook installed; fixed ops disabled: " +
              (disabled_reason.empty() ? std::string("empty op mask") : disabled_reason);
    }

    for_each_dispatcher_runtime_slot([&](int index, DispatcherRuntimeSlot &runtime) {
        if (runtime.permanently_bound && !active[static_cast<size_t>(index)])
            disable_dispatcher_runtime_slot(index);
    });
}

void configure_dispatcher_runtime_slot(int index,
                                       uint32_t slot_id,
                                       uint32_t target_kind,
                                       uint64_t before_op_mask,
                                       uint64_t after_op_mask) {
    auto *runtime = dispatcher_runtime_slot(index);
    if (runtime == nullptr)
        return;

    runtime->enabled.store(0, std::memory_order_release);
    runtime->before_op_mask.store(before_op_mask, std::memory_order_release);
    runtime->after_op_mask.store(after_op_mask, std::memory_order_release);
    runtime->slot_id.store(slot_id, std::memory_order_release);
    runtime->target_kind.store(target_kind, std::memory_order_release);
    runtime->call_count.store(0, std::memory_order_release);
    runtime->fault_count.store(0, std::memory_order_release);
}

bool publish_original_to_dispatcher(int index,
                                    void *original,
                                    const std::string &key,
                                    const std::string &abi_kind,
                                    void *function) {
    auto *runtime = dispatcher_runtime_slot(index);
    if (runtime == nullptr)
        return false;

    if (runtime->permanently_bound &&
        (runtime->bound_key != key ||
         runtime->bound_abi_kind != abi_kind ||
         runtime->bound_function != function)) {
        return false;
    }
    runtime->permanently_bound = true;
    runtime->bound_key = key;
    runtime->bound_abi_kind = abi_kind;
    runtime->bound_function = function;
    runtime->original.store(original, std::memory_order_release);
    runtime->enabled.store(original == nullptr ? 0u : 1u, std::memory_order_release);
    return original != nullptr;
}

struct FixedOpArgs {
    uint32_t target_kind = kTargetKindUnknown;
    void *instance = nullptr;
    bool has_player_count = false;
    int32_t player_count = 0;
    bool has_play_args = false;
    int32_t seq_id = 0;
    bool is_restart = false;
    bool has_wipe_direction = false;
    int32_t wipe_direction = 0;
    bool has_reset_to_editor = false;
    bool reset_to_editor = false;
    bool has_hit_margin = false;
    int32_t hit_margin = 0;
    bool has_floor_exit_angle = false;
    float floor_exit_angle = 0.0f;
    bool has_floor_move_hit_margin = false;
    int32_t floor_move_hit_margin = 0;
    bool has_player_hit_is_auto = false;
    bool player_hit_is_auto = false;
    bool has_death_args = false;
    bool death_overload = false;
    bool death_multipress = false;
    bool death_hitbox = false;
    bool has_hit_timing = false;
    float hit_timing_ms = 0.0f;
    int32_t hit_timing_margin = 0;
    float bpm_times_speed = 0.0f;
    float conductor_pitch = 0.0f;
    bool has_bool_result = false;
    bool bool_result = false;
    bool has_gameplay_input_args = false;
    bool gameplay_input_is_auto = false;
    int32_t gameplay_input_state = 0;
    // Generic capture for managed-event rules: every dispatcher fills the raw
    // argument slots (pointers as-is, float/double bit patterns) in declaration
    // order. The named fields above remain the fixed-op view of the same call.
    uint64_t raw_args[kManagedEventMaxArgs] = {};
    uint32_t raw_arg_count = 0;
    uint64_t invocation_id = 0;
    uint32_t managed_result_kind = 0;
    uint32_t managed_run_original = 1;
    uint32_t managed_result_valid = 0;
    uint64_t managed_result_value = 0;
};

void capture_raw_arg(FixedOpArgs &args, int index, uint64_t value) {
    if (index < 0 || index >= kManagedEventMaxArgs)
        return;
    args.raw_args[static_cast<uint32_t>(index)] = value;
    args.raw_arg_count = std::max(args.raw_arg_count, static_cast<uint32_t>(index + 1));
}

void capture_raw_pointer(FixedOpArgs &args, int index, const void *value) {
    capture_raw_arg(args, index, reinterpret_cast<uint64_t>(reinterpret_cast<uintptr_t>(value)));
}

void capture_raw_float(FixedOpArgs &args, int index, float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    capture_raw_arg(args, index, bits);
}

void capture_raw_double(FixedOpArgs &args, int index, double value) {
    uint64_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    capture_raw_arg(args, index, bits);
}

bool managed_event_needs_hit_snapshot(uint32_t target_kind) {
    return target_kind == kTargetKindMarginTrackerAddHit ||
           target_kind == kTargetKindMarginTrackerReset ||
           target_kind == kTargetKindMarginTrackerCalculatePercentAcc ||
           target_kind == kTargetKindMistakesSetPlayerCount ||
           target_kind == kTargetKindScnGamePlay ||
           target_kind == kTargetKindPressToStartShowText ||
           target_kind == kTargetKindUiControllerWipeToBlack ||
           target_kind == kTargetKindEditorResetScene ||
           target_kind == kTargetKindEditorSwitchToEditMode ||
           target_kind == kTargetKindControllerStartLoadingScene;
}

void capture_managed_event_hit_snapshot(PcCompatManagedEventV2 &event) {
    event.hit_snapshot_attached = 1;
    for (int attempt = 0; attempt < 3; ++attempt) {
        const uint32_t sequence_before =
            g_hit_margin_snapshot.sequence.load(std::memory_order_acquire);
        if ((sequence_before & 1u) != 0)
            continue;

        const uint32_t valid =
            g_hit_margin_snapshot.valid.load(std::memory_order_relaxed);
        const uint32_t length = std::min(
            g_hit_margin_snapshot.length.load(std::memory_order_relaxed),
            kManagedEventHitMarginCount);
        event.hit_snapshot_generation =
            g_hit_margin_snapshot.generation.load(std::memory_order_relaxed);
        event.hit_snapshot_valid = valid;
        event.hit_snapshot_length = valid != 0 ? length : 0;
        for (uint32_t index = 0; index < event.hit_snapshot_length; ++index) {
            event.hit_snapshot_counts[index] =
                g_hit_margin_snapshot.counts[index].load(std::memory_order_relaxed);
        }

        const uint32_t sequence_after =
            g_hit_margin_snapshot.sequence.load(std::memory_order_acquire);
        if (sequence_before == sequence_after && (sequence_after & 1u) == 0)
            return;
    }

    event.hit_snapshot_generation = 0;
    event.hit_snapshot_valid = 0;
    event.hit_snapshot_length = 0;
    event.hit_snapshot_attached = 0;
}

void push_managed_event(ManagedEventRing *ring,
                        const PcCompatManagedEventV2 &event,
                        bool lifecycle_boundary) {
    if (ring == nullptr || ring->retired.load(std::memory_order_acquire) != 0)
        return;

    const ManagedQueuedEvent queued_event{
        .event = event,
        .lifecycle_boundary = lifecycle_boundary,
    };
    std::lock_guard<std::mutex> guard(ring->lock);
    if (ring->retired.load(std::memory_order_relaxed) != 0)
        return;
    if (ring->enabled.load(std::memory_order_relaxed) == 0) {
        if (lifecycle_boundary) {
            ring->pending_lifecycle_event = queued_event;
            ring->has_pending_lifecycle_event = true;
        }
        return;
    }
    auto &queue = ring->events;
    // Lifecycle callbacks delimit the validity of every raw IL2CPP pointer in the
    // queue. They must not wait behind callbacks from the scene being hidden or
    // replaced: those objects may already be destroyed by the time managed code
    // drains them. Treat the boundary as a barrier and keep only the new lifecycle
    // event. Ordinary callback ordering within one scene remains FIFO.
    if (lifecycle_boundary && ring->count != 0) {
        ring->dropped += ring->count;
        ring->head = 0;
        ring->count = 0;
    }
    if (!lifecycle_boundary &&
        ring->count >= queue.size() - kManagedEventLifecycleReserve) {
        ++ring->dropped;
        return;
    }
    if (ring->count == queue.size()) {
        ring->head = (ring->head + 1) % queue.size();
        --ring->count;
        ++ring->dropped;
    }
    const size_t tail = (ring->head + ring->count) % queue.size();
    queue[tail] = queued_event;
    ++ring->count;
    ++ring->pushed;
}

// Hook-thread managed-event fan-out. Reads the per-dispatcher rule snapshot (built
// under g_lock at install/sync time) and pushes one event per enabled rule into the
// owning bundle's ring. No Unity API and no g_lock access on this path.
void enqueue_managed_event_rules(int dispatcher_index, const FixedOpArgs &args) {
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    if (runtime == nullptr || runtime->enabled.load(std::memory_order_acquire) == 0)
        return;

    const auto targets = std::atomic_load_explicit(
        &runtime->managed_event_after_rules,
        std::memory_order_acquire);
    if (!targets)
        return;

    PcCompatManagedEventV2 event{};
    event.arg_count = std::min(
        args.raw_arg_count,
        static_cast<uint32_t>(kManagedEventMaxArgs));
    event.instance_ptr = reinterpret_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(args.instance));
    event.invocation_id = args.invocation_id;
    event.result_kind = args.managed_result_kind;
    event.result_valid = args.managed_result_valid;
    event.result_value = args.managed_result_value;
    event.run_original = args.managed_run_original;
    for (uint32_t index = 0; index < event.arg_count; ++index)
        event.args[index] = args.raw_args[index];
    if (managed_event_needs_hit_snapshot(args.target_kind))
        capture_managed_event_hit_snapshot(event);

    const uint64_t sequence_base = g_managed_event_dispatch_sequence.fetch_add(
        static_cast<uint64_t>(targets->size()),
        std::memory_order_relaxed);
    for (size_t target_index = 0; target_index < targets->size(); ++target_index) {
        const auto &target = (*targets)[target_index];
        if (!pc_mod_session_token_active(target.pc_mod_session) ||
            target.ring == nullptr ||
            target.ring->retired.load(std::memory_order_acquire) != 0 ||
            (target.ring->enabled.load(std::memory_order_acquire) == 0 &&
             !target.lifecycle_boundary)) {
            continue;
        }
        event.patch_id = target.managed_event_id;
        event.dispatch_sequence = sequence_base + static_cast<uint64_t>(target_index);
        push_managed_event(
            target.ring.get(),
            event,
            target.lifecycle_boundary);
    }
}

bool run_managed_prefix_rules(int dispatcher_index, FixedOpArgs &args) {
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    if (runtime == nullptr || runtime->enabled.load(std::memory_order_acquire) == 0)
        return false;

    const auto targets = std::atomic_load_explicit(
        &runtime->managed_prefix_before_rules,
        std::memory_order_acquire);
    const auto callback = g_managed_prefix_callback.load(std::memory_order_acquire);
    if (!targets || targets->empty() || callback == nullptr)
        return false;

    if (g_managed_prefix_callback_depth >= 32) {
        runtime->fault_count.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    // Before any invocation struct is built. When every target is owner-filtered - the render
    // callback case - and the instance is not a registered MOD host, there is nothing to do and the
    // game's own calls fall straight through to the original.
    const uint64_t instance_ptr = reinterpret_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(args.instance));
    bool any_dispatchable = false;
    for (const auto &target : *targets) {
        if (!target.owner_filtered || is_managed_render_host(instance_ptr)) {
            any_dispatchable = true;
            break;
        }
    }
    if (!any_dispatchable)
        return false;

    ++g_managed_prefix_callback_depth;
    PcCompatManagedPrefixInvocationV2 invocation{};
    invocation.struct_size = sizeof(invocation);
    invocation.abi_version = 2;
    invocation.argument_count = std::min(
        args.raw_arg_count,
        static_cast<uint32_t>(kManagedEventMaxArgs));
    invocation.result_kind = args.managed_result_kind;
    invocation.instance_ptr = reinterpret_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(args.instance));
    invocation.invocation_id = args.invocation_id;
    invocation.run_original = args.managed_run_original;
    invocation.result_valid = args.managed_result_valid;
    invocation.result_value = args.managed_result_value;
    for (uint32_t index = 0; index < invocation.argument_count; ++index)
        invocation.arguments[index] = args.raw_args[index];

    for (const auto &target : *targets) {
        if (!pc_mod_session_token_active(target.pc_mod_session) ||
            target.ring == nullptr)
            continue;
        // Re-checked per target rather than hoisted: a mixed slot can hold both an ordinary prefix
        // and a render callback, and only the latter is instance-scoped.
        if (target.owner_filtered && !is_managed_render_host(instance_ptr))
            continue;
        {
            std::lock_guard<std::mutex> prefix_lock(
                target.ring->prefix_lifecycle_lock);
            if (target.ring->retired.load(std::memory_order_acquire) != 0)
                continue;
            ++target.ring->in_flight_prefixes;
        }
        const int result = callback(
            target.bundle_id,
            target.managed_prefix_id,
            &invocation);
        {
            std::lock_guard<std::mutex> prefix_lock(
                target.ring->prefix_lifecycle_lock);
            --target.ring->in_flight_prefixes;
            if (target.ring->in_flight_prefixes == 0)
                target.ring->prefix_lifecycle_condition.notify_all();
        }
        if (result < 0) {
            runtime->fault_count.fetch_add(1, std::memory_order_relaxed);
        }
    }

    args.managed_run_original = invocation.run_original;
    args.managed_result_valid = invocation.result_valid;
    args.managed_result_value = invocation.result_value;
    args.instance = reinterpret_cast<void *>(
        static_cast<uintptr_t>(invocation.instance_ptr));
    for (uint32_t index = 0; index < invocation.argument_count; ++index)
        args.raw_args[index] = invocation.arguments[index];
    --g_managed_prefix_callback_depth;
    return invocation.run_original == 0;
}

uint32_t raw_u32_argument(const FixedOpArgs &args, int index) {
    if (index < 0 || static_cast<uint32_t>(index) >= args.raw_arg_count)
        return 0;
    return static_cast<uint32_t>(args.raw_args[index]);
}

int32_t raw_i32_argument(const FixedOpArgs &args, int index) {
    return static_cast<int32_t>(raw_u32_argument(args, index));
}

bool raw_bool_argument(const FixedOpArgs &args, int index) {
    return raw_u32_argument(args, index) != 0;
}

float raw_float_argument(const FixedOpArgs &args, int index) {
    uint32_t bits = raw_u32_argument(args, index);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

void refresh_fixed_args_after_managed_prefix(FixedOpArgs &args) {
    switch (args.target_kind) {
    case kTargetKindEditorResetScene:
    case kTargetKindEditorSwitchToEditMode:
        args.reset_to_editor = raw_bool_argument(args, 0);
        args.has_reset_to_editor = true;
        break;
    case kTargetKindControllerStartLoadingScene:
        args.wipe_direction = raw_i32_argument(args, 0);
        args.has_wipe_direction = true;
        break;
    case kTargetKindMarginTrackerAddHit:
        args.hit_margin = raw_i32_argument(args, 0);
        args.has_hit_margin = true;
        break;
    case kTargetKindPlanetMoveToNextFloor:
        args.floor_exit_angle = raw_float_argument(args, 1);
        args.floor_move_hit_margin = raw_i32_argument(args, 2);
        args.has_floor_exit_angle = true;
        args.has_floor_move_hit_margin = true;
        break;
    case kTargetKindUiControllerWipeToBlack:
        args.wipe_direction = raw_i32_argument(args, 0);
        args.has_wipe_direction = true;
        break;
    case kTargetKindPlayerDie:
        args.death_overload = raw_bool_argument(args, 0);
        args.death_multipress = raw_bool_argument(args, 1);
        args.death_hitbox = raw_bool_argument(args, 3);
        args.has_death_args = true;
        break;
    case kTargetKindPlayerHit:
        args.player_hit_is_auto = raw_bool_argument(args, 0);
        args.has_player_hit_is_auto = true;
        break;
    case kTargetKindScnGamePlay:
        args.seq_id = raw_i32_argument(args, 0);
        args.is_restart = raw_bool_argument(args, 1);
        args.has_play_args = true;
        break;
    case kTargetKindPlayerHitInputEvent:
        args.gameplay_input_is_auto = raw_bool_argument(args, 0);
        args.gameplay_input_state = raw_i32_argument(args, 1);
        args.has_gameplay_input_args = true;
        break;
    case kTargetKindMistakesSetPlayerCount:
        args.player_count = raw_i32_argument(args, 0);
        args.has_player_count = true;
        break;
    default:
        break;
    }
}

FixedOpArgs make_fixed_op_args(int dispatcher_index, void *instance = nullptr) {
    FixedOpArgs args;
    args.instance = instance;
    args.invocation_id = g_managed_prefix_invocation_sequence.fetch_add(
        1,
        std::memory_order_relaxed);
    if (auto *runtime = dispatcher_runtime_slot(dispatcher_index); runtime != nullptr)
        args.target_kind = runtime->target_kind.load(std::memory_order_acquire);
    return args;
}

bool read_instance_float(void *instance, int32_t offset, float &value) {
    if (instance == nullptr || offset <= 0)
        return false;
    std::memcpy(&value, static_cast<const char *>(instance) + offset, sizeof(value));
    return std::isfinite(value);
}

void free_resource_handle(void *handle) {
    if (handle != nullptr && g_il2cpp_metadata.gchandle_free != nullptr)
        g_il2cpp_metadata.gchandle_free(handle);
}

void *new_resource_handle(void *object) {
    return object != nullptr && g_il2cpp_metadata.gchandle_new != nullptr
        ? g_il2cpp_metadata.gchandle_new(object, false)
        : nullptr;
}

void track_resource_object(
    std::vector<void *> &handles,
    std::set<void *> &objects,
    void *object) {
    if (object == nullptr || g_il2cpp_metadata.gchandle_get_target == nullptr)
        return;
    std::lock_guard<std::mutex> guard(g_resource_tracked_lock);
    const auto inserted = objects.insert(object);
    if (!inserted.second)
        return;
    if (void *handle = new_resource_handle(object); handle != nullptr) {
        handles.push_back(handle);
    } else {
        objects.erase(inserted.first);
    }
}

void track_resource_single(void *&slot, void *object) {
    if (object == nullptr || g_il2cpp_metadata.gchandle_get_target == nullptr)
        return;
    std::lock_guard<std::mutex> guard(g_resource_tracked_lock);
    if (slot != nullptr && g_il2cpp_metadata.gchandle_get_target(slot) == object)
        return;
    void *next = new_resource_handle(object);
    if (next == nullptr)
        return;
    void *stale = slot;
    slot = next;
    free_resource_handle(stale);
}

void apply_resource_floor_color(void *floor) {
    if (!resource_change_tile_color_enabled() || floor == nullptr)
        return;
    if (!resource_floor_is_beat(floor))
        return;

    std::string error;
    if (!ensure_resource_runtime_cache(error)) {
        static std::atomic<uint32_t> logged{0};
        if (logged.fetch_add(1, std::memory_order_relaxed) < 3)
            LOGI("ResourceChanger floor color skipped: %s", error.c_str());
        return;
    }

    void *method = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        method = g_resource_cache.scr_floor_set_color;
    }
    if (method != nullptr)
        invoke_void_color(method, floor, resource_tile_color());
    track_resource_object(g_resource_floor_handles, g_resource_floor_objects, floor);
}

void apply_resource_planet_color(void *planet) {
    if (!resource_change_ball_color_enabled() || planet == nullptr || resource_is_coop_mode())
        return;

    std::string error;
    if (!ensure_resource_runtime_cache(error)) {
        static std::atomic<uint32_t> logged{0};
        if (logged.fetch_add(1, std::memory_order_relaxed) < 3)
            LOGI("ResourceChanger planet color skipped: %s", error.c_str());
        return;
    }

    void *renderer = nullptr;
    void *disable_all = nullptr;
    void *set_planet_color = nullptr;
    void *set_tail_color = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        renderer = read_object_field(planet, g_resource_cache.scr_planet_planet_renderer_offset);
        disable_all = g_resource_cache.planet_renderer_disable_all;
        set_planet_color = g_resource_cache.planet_renderer_set_planet_color;
        set_tail_color = g_resource_cache.planet_renderer_set_tail_color;
    }
    if (renderer == nullptr)
        return;

    invoke_il2cpp_method(disable_all, renderer, nullptr);
    apply_resource_planet_white_texture(renderer);
    const auto color = resource_planet_color();
    invoke_void_color(set_planet_color, renderer, color);
    invoke_void_color(set_tail_color, renderer, color);
    track_resource_object(g_resource_planet_handles, g_resource_planet_objects, planet);
}

void apply_resource_logo_text(void *logo_text) {
    if (logo_text == nullptr)
        return;
    track_resource_single(g_resource_logo_text_handle, logo_text);

    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return;
    void *color_logo = nullptr;
    void *component_get_game_object = nullptr;
    void *component_get_transform = nullptr;
    void *game_object_get_component = nullptr;
    void *game_object_get_transform = nullptr;
    void *game_object_set_active = nullptr;
    void *object_instantiate = nullptr;
    void *object_set_name = nullptr;
    void *transform_get_parent = nullptr;
    void *transform_find = nullptr;
    void *transform_set_parent = nullptr;
    void *rect_get_anchored_position = nullptr;
    void *rect_set_anchored_position = nullptr;
    void *text_set_text = nullptr;
    void *text_set_font_size = nullptr;
    void *graphic_set_color = nullptr;
    void *rect_transform_type = nullptr;
    void *text_type = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        color_logo = g_resource_cache.scr_logo_text_color_logo;
        component_get_game_object = g_resource_cache.component_get_game_object;
        component_get_transform = g_resource_cache.component_get_transform;
        game_object_get_component = g_resource_cache.game_object_get_component;
        game_object_get_transform = g_resource_cache.game_object_get_transform;
        game_object_set_active = g_resource_cache.game_object_set_active;
        object_instantiate = g_resource_cache.object_instantiate;
        object_set_name = g_resource_cache.object_set_name;
        transform_get_parent = g_resource_cache.transform_get_parent;
        transform_find = g_resource_cache.transform_find;
        transform_set_parent = g_resource_cache.transform_set_parent;
        rect_get_anchored_position = g_resource_cache.rect_transform_get_anchored_position;
        rect_set_anchored_position = g_resource_cache.rect_transform_set_anchored_position;
        text_set_text = g_resource_cache.text_set_text;
        text_set_font_size = g_resource_cache.text_set_font_size;
        graphic_set_color = g_resource_cache.graphic_set_color;
        rect_transform_type = g_resource_cache.rect_transform_type_object;
        text_type = g_resource_cache.text_type_object;
    }
    if (component_get_game_object == nullptr || component_get_transform == nullptr ||
        game_object_get_component == nullptr || game_object_get_transform == nullptr ||
        transform_get_parent == nullptr || transform_find == nullptr ||
        rect_transform_type == nullptr || text_type == nullptr)
        return;

    void *logo_game_object = invoke_object_noargs(component_get_game_object, logo_text);
    void *rect_args[] = {rect_transform_type};
    void *logo_rect = logo_game_object != nullptr
        ? invoke_il2cpp_method(game_object_get_component, logo_game_object, rect_args)
        : nullptr;
    if (logo_rect != nullptr && rect_get_anchored_position != nullptr &&
        rect_set_anchored_position != nullptr) {
        void *boxed = invoke_object_noargs(rect_get_anchored_position, logo_rect);
        void *unboxed = boxed != nullptr && g_il2cpp_metadata.object_unbox != nullptr
            ? g_il2cpp_metadata.object_unbox(boxed)
            : nullptr;
        if (unboxed != nullptr) {
            Vector2Value anchored = *static_cast<Vector2Value *>(unboxed);
            anchored.y = 0.75f;
            void *args[] = {&anchored};
            invoke_il2cpp_method(rect_set_anchored_position, logo_rect, args);
        }
    }

    if (resource_change_ball_color_enabled() && !resource_is_coop_mode() &&
        color_logo != nullptr) {
        const ColorValue color = resource_planet_color();
        invoke_logo_color(color_logo, logo_text, color, true);
        invoke_logo_color(color_logo, logo_text, color, false);
    }

    void *logo_transform = invoke_object_noargs(component_get_transform, logo_text);
    void *parent = logo_transform != nullptr
        ? invoke_object_noargs(transform_get_parent, logo_transform)
        : nullptr;
    parent = parent != nullptr ? invoke_object_noargs(transform_get_parent, parent) : nullptr;
    if (parent == nullptr || g_il2cpp_metadata.string_new == nullptr)
        return;

    void *hit_space_name = g_il2cpp_metadata.string_new("Hit Space");
    void *find_hit_args[] = {hit_space_name};
    void *hit_space = invoke_il2cpp_method(transform_find, parent, find_hit_args);
    if (hit_space == nullptr)
        return;

    void *clone_name = g_il2cpp_metadata.string_new("JipperResourcepack Logo");
    void *find_clone_args[] = {clone_name};
    void *clone_transform = invoke_il2cpp_method(
        transform_find,
        hit_space,
        find_clone_args);
    void *clone = clone_transform != nullptr
        ? invoke_object_noargs(component_get_game_object, clone_transform)
        : nullptr;
    if (clone == nullptr) {
        void *education_name = g_il2cpp_metadata.string_new("Education Edition");
        void *find_education_args[] = {education_name};
        void *education_transform = invoke_il2cpp_method(
            transform_find,
            hit_space,
            find_education_args);
        void *education_game_object = education_transform != nullptr
            ? invoke_object_noargs(component_get_game_object, education_transform)
            : nullptr;
        if (education_game_object == nullptr || object_instantiate == nullptr)
            return;

        void *instantiate_args[] = {education_game_object};
        clone = invoke_il2cpp_method(object_instantiate, nullptr, instantiate_args);
        if (clone == nullptr)
            return;
        clone_transform = invoke_object_noargs(game_object_get_transform, clone);
        if (clone_transform != nullptr && transform_set_parent != nullptr) {
            bool world_position_stays = false;
            void *parent_args[] = {hit_space, &world_position_stays};
            invoke_il2cpp_method(transform_set_parent, clone_transform, parent_args);
        }
    }
    if (game_object_set_active != nullptr) {
        bool active = true;
        void *active_args[] = {&active};
        invoke_il2cpp_method(game_object_set_active, clone, active_args);
    }
    if (object_set_name != nullptr) {
        void *name_args[] = {clone_name};
        invoke_il2cpp_method(object_set_name, clone, name_args);
    }

    void *text_args[] = {text_type};
    void *text = invoke_il2cpp_method(game_object_get_component, clone, text_args);
    if (text != nullptr) {
        std::string resource_pack_name;
        {
            std::lock_guard<std::mutex> guard(g_resource_state_lock);
            resource_pack_name = g_resource_pack_name;
        }
        if (text_set_text != nullptr) {
            void *value = g_il2cpp_metadata.string_new(resource_pack_name.c_str());
            void *args[] = {value};
            invoke_il2cpp_method(text_set_text, text, args);
        }
        if (graphic_set_color != nullptr)
            invoke_void_color(graphic_set_color, text, resource_title_color());
        if (text_set_font_size != nullptr) {
            int32_t font_size = 100;
            void *args[] = {&font_size};
            invoke_il2cpp_method(text_set_font_size, text, args);
        }
    }

    void *clone_rect = invoke_il2cpp_method(game_object_get_component, clone, rect_args);
    if (clone_rect != nullptr && rect_set_anchored_position != nullptr) {
        Vector2Value anchored{-50.0f, 330.0f};
        void *args[] = {&anchored};
        invoke_il2cpp_method(rect_set_anchored_position, clone_rect, args);
    }
}

void *load_resource_rabbit_sprite() {
    std::lock_guard<std::mutex> guard(g_resource_asset_lock);
    if (g_resource_rabbit_sprite_handle == nullptr ||
        g_il2cpp_metadata.gchandle_get_target == nullptr)
        return nullptr;
    return g_il2cpp_metadata.gchandle_get_target(g_resource_rabbit_sprite_handle);
}

void apply_resource_editor_rabbit(void *editor) {
    if (editor == nullptr || !resource_change_rabbit_enabled())
        return;

    std::string error;
    if (!ensure_resource_runtime_cache(error))
        return;

    int32_t auto_image_offset = -1;
    void *set_sprite = nullptr;
    void *get_sprite = nullptr;
    void *set_color = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        auto_image_offset = g_resource_cache.scn_editor_auto_image_offset;
        set_sprite = g_resource_cache.image_set_sprite;
        get_sprite = g_resource_cache.image_get_sprite;
        set_color = g_resource_cache.image_set_color;
    }
    void *image = read_object_field(editor, auto_image_offset);
    void *sprite = load_resource_rabbit_sprite();
    if (image == nullptr || sprite == nullptr || set_sprite == nullptr)
        return;

    void *current = get_sprite != nullptr ? invoke_object_noargs(get_sprite, image) : nullptr;
    if (current != sprite) {
        {
            std::lock_guard<std::mutex> guard(g_resource_tracked_lock);
            void *tracked_editor = g_resource_editor_handle != nullptr &&
                                   g_il2cpp_metadata.gchandle_get_target != nullptr
                ? g_il2cpp_metadata.gchandle_get_target(g_resource_editor_handle)
                : nullptr;
            if (tracked_editor != editor) {
                free_resource_handle(g_resource_editor_handle);
                free_resource_handle(g_resource_original_rabbit_sprite_handle);
                g_resource_editor_handle = new_resource_handle(editor);
                g_resource_original_rabbit_sprite_handle = nullptr;
            }
            if (current != nullptr && current != sprite &&
                g_resource_original_rabbit_sprite_handle == nullptr)
                g_resource_original_rabbit_sprite_handle = new_resource_handle(current);
        }

        void *args[] = {sprite};
        invoke_il2cpp_method(set_sprite, image, args);
    }
    if (set_color != nullptr)
        invoke_void_color(set_color, image, resource_rabbit_color(resource_auto_enabled()));
}

void set_resource_logo_clone_active(void *logo_text, bool active) {
    if (logo_text == nullptr)
        return;

    void *component_get_game_object = nullptr;
    void *component_get_transform = nullptr;
    void *transform_get_parent = nullptr;
    void *transform_find = nullptr;
    void *game_object_set_active = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        component_get_game_object = g_resource_cache.component_get_game_object;
        component_get_transform = g_resource_cache.component_get_transform;
        transform_get_parent = g_resource_cache.transform_get_parent;
        transform_find = g_resource_cache.transform_find;
        game_object_set_active = g_resource_cache.game_object_set_active;
    }
    if (component_get_game_object == nullptr ||
        component_get_transform == nullptr ||
        transform_get_parent == nullptr ||
        transform_find == nullptr ||
        game_object_set_active == nullptr ||
        g_il2cpp_metadata.string_new == nullptr)
        return;

    void *logo_transform = invoke_object_noargs(component_get_transform, logo_text);
    void *parent = logo_transform != nullptr
        ? invoke_object_noargs(transform_get_parent, logo_transform)
        : nullptr;
    parent = parent != nullptr
        ? invoke_object_noargs(transform_get_parent, parent)
        : nullptr;
    if (parent == nullptr)
        return;

    void *hit_space_name = g_il2cpp_metadata.string_new("Hit Space");
    void *find_hit_args[] = {hit_space_name};
    void *hit_space = invoke_il2cpp_method(transform_find, parent, find_hit_args);
    if (hit_space == nullptr)
        return;
    void *clone_name = g_il2cpp_metadata.string_new("JipperResourcepack Logo");
    void *find_clone_args[] = {clone_name};
    void *clone_transform = invoke_il2cpp_method(transform_find, hit_space, find_clone_args);
    void *clone = clone_transform != nullptr
        ? invoke_object_noargs(component_get_game_object, clone_transform)
        : nullptr;
    if (clone == nullptr)
        return;

    bool next_active = active;
    void *active_args[] = {&next_active};
    invoke_il2cpp_method(game_object_set_active, clone, active_args);
}

int apply_pending_resource_changer_state() {
    const uint32_t mask = g_resource_pending_restore_mask.exchange(
        0,
        std::memory_order_acq_rel);
    if (mask == 0)
        return 0;

    std::string error;
    if (!ensure_resource_runtime_cache(error)) {
        g_resource_pending_restore_mask.fetch_or(mask, std::memory_order_release);
        LOGI("ResourceChanger restore deferred: %s", error.c_str());
        return -1;
    }

    void *editor_handle = nullptr;
    void *original_rabbit_handle = nullptr;
    void *logo_handle = nullptr;
    std::vector<void *> planets;
    std::vector<void *> floors;
    {
        std::lock_guard<std::mutex> guard(g_resource_tracked_lock);
        if ((mask & kResourceRestoreRabbit) != 0) {
            editor_handle = g_resource_editor_handle;
            original_rabbit_handle = g_resource_original_rabbit_sprite_handle;
            g_resource_editor_handle = nullptr;
            g_resource_original_rabbit_sprite_handle = nullptr;
        }
        if ((mask & kResourceRestorePlanet) != 0) {
            planets.swap(g_resource_planet_handles);
            g_resource_planet_objects.clear();
            logo_handle = g_resource_logo_text_handle;
            g_resource_logo_text_handle = nullptr;
        }
        if ((mask & kResourceRestoreTile) != 0) {
            floors.swap(g_resource_floor_handles);
            g_resource_floor_objects.clear();
        }
    }

    int restored = 0;
    void *editor = editor_handle != nullptr && g_il2cpp_metadata.gchandle_get_target != nullptr
        ? g_il2cpp_metadata.gchandle_get_target(editor_handle)
        : nullptr;
    void *original_rabbit = original_rabbit_handle != nullptr &&
                            g_il2cpp_metadata.gchandle_get_target != nullptr
        ? g_il2cpp_metadata.gchandle_get_target(original_rabbit_handle)
        : nullptr;
    if (editor != nullptr && original_rabbit != nullptr) {
        int32_t image_offset = -1;
        void *set_sprite = nullptr;
        void *otto_update = nullptr;
        {
            std::lock_guard<std::mutex> guard(g_resource_cache_lock);
            image_offset = g_resource_cache.scn_editor_auto_image_offset;
            set_sprite = g_resource_cache.image_set_sprite;
            otto_update = g_resource_cache.scn_editor_otto_update;
        }
        void *image = read_object_field(editor, image_offset);
        if (image != nullptr && set_sprite != nullptr) {
            void *args[] = {original_rabbit};
            invoke_il2cpp_method(set_sprite, image, args);
            if (otto_update != nullptr)
                invoke_il2cpp_method(otto_update, editor, nullptr);
            ++restored;
        }
    }
    free_resource_handle(editor_handle);
    free_resource_handle(original_rabbit_handle);
    if (editor != nullptr && resource_change_rabbit_enabled())
        apply_resource_editor_rabbit(editor);

    int32_t renderer_offset = -1;
    int32_t is_red_offset = -1;
    void *load_planet_color = nullptr;
    void *set_floor_color = nullptr;
    void *update_logo_colors = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_cache_lock);
        renderer_offset = g_resource_cache.scr_planet_planet_renderer_offset;
        is_red_offset = g_resource_cache.scr_planet_is_red_offset;
        load_planet_color = g_resource_cache.planet_renderer_load_planet_color;
        set_floor_color = g_resource_cache.scr_floor_set_color;
        update_logo_colors = g_resource_cache.scr_logo_text_update_colors;
    }
    for (void *handle : planets) {
        void *planet = g_il2cpp_metadata.gchandle_get_target != nullptr
            ? g_il2cpp_metadata.gchandle_get_target(handle)
            : nullptr;
        void *renderer = planet != nullptr
            ? read_object_field(planet, renderer_offset)
            : nullptr;
        if (renderer != nullptr && load_planet_color != nullptr &&
            is_red_offset > 0) {
            bool is_red = false;
            std::memcpy(&is_red, static_cast<char *>(planet) + is_red_offset, sizeof(is_red));
            void *args[] = {&is_red};
            invoke_il2cpp_method(load_planet_color, renderer, args);
            ++restored;
        }
        if (planet != nullptr && resource_change_ball_color_enabled())
            apply_resource_planet_color(planet);
        free_resource_handle(handle);
    }
    for (void *handle : floors) {
        void *floor = g_il2cpp_metadata.gchandle_get_target != nullptr
            ? g_il2cpp_metadata.gchandle_get_target(handle)
            : nullptr;
        if (floor != nullptr && set_floor_color != nullptr) {
            invoke_void_color(
                set_floor_color,
                floor,
                ColorValue{0.675f, 0.675f, 0.766f, 1.0f});
            ++restored;
        }
        if (floor != nullptr && resource_change_tile_color_enabled())
            apply_resource_floor_color(floor);
        free_resource_handle(handle);
    }
    void *logo = logo_handle != nullptr && g_il2cpp_metadata.gchandle_get_target != nullptr
        ? g_il2cpp_metadata.gchandle_get_target(logo_handle)
        : nullptr;
    if (logo != nullptr && update_logo_colors != nullptr) {
        invoke_il2cpp_method(update_logo_colors, logo, nullptr);
        ++restored;
    }
    if (logo != nullptr) {
        if (resource_has_active_contribution())
            apply_resource_logo_text(logo);
        else
            set_resource_logo_clone_active(logo, false);
    }
    free_resource_handle(logo_handle);
    return restored;
}

void release_resource_scene_handles() {
    std::vector<void *> handles;
    {
        std::lock_guard<std::mutex> guard(g_resource_tracked_lock);
        handles.swap(g_resource_planet_handles);
        g_resource_planet_objects.clear();
        handles.insert(
            handles.end(),
            g_resource_floor_handles.begin(),
            g_resource_floor_handles.end());
        g_resource_floor_handles.clear();
        g_resource_floor_objects.clear();
        if (g_resource_logo_text_handle != nullptr) {
            handles.push_back(g_resource_logo_text_handle);
            g_resource_logo_text_handle = nullptr;
        }
    }
    for (void *handle : handles)
        free_resource_handle(handle);
}

struct FixedBeforeArgs {
    uint32_t target_kind = kTargetKindUnknown;
    void *instance = nullptr;
    ColorValue *color_arg0 = nullptr;
};

uint32_t dispatcher_target_kind(int dispatcher_index) {
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    return runtime != nullptr
        ? runtime->target_kind.load(std::memory_order_acquire)
        : kTargetKindUnknown;
}

bool pc_mod_session_token_active(const PcModSessionToken &session) {
    return session.session_handle == 0 ||
        pccompat_session_active(
            session.session_handle,
            session.host_generation,
            session.resource_generation) == 1;
}

bool load_active_session_rule_masks(const DispatcherRuntimeSlot *runtime,
                                    uint64_t *before_mask,
                                    uint64_t *after_mask) {
    if (runtime == nullptr || before_mask == nullptr || after_mask == nullptr)
        return false;
    *before_mask = 0;
    *after_mask = 0;
    const auto bindings = std::atomic_load_explicit(
        &runtime->session_rule_masks, std::memory_order_acquire);
    if (bindings == nullptr || bindings->empty()) {
        *before_mask = runtime->before_op_mask.load(std::memory_order_acquire);
        *after_mask = runtime->after_op_mask.load(std::memory_order_acquire);
        return (*before_mask | *after_mask) != 0;
    }
    bool active = false;
    for (const auto &binding : *bindings) {
        if (!pc_mod_session_token_active(binding.pc_mod_session))
            continue;
        *before_mask |= binding.before_op_mask;
        *after_mask |= binding.after_op_mask;
        active = true;
    }
    return active;
}

bool pc_mod_resource_session_active(const char *mod_id,
                                    int64_t resource_generation) {
    if (mod_id == nullptr || *mod_id == '\0' || resource_generation <= 0)
        return false;
    return true;
}

bool run_fixed_before_ops(int dispatcher_index, const FixedBeforeArgs &args) {
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    if (runtime == nullptr)
        return false;

    if (g_resource_pending_restore_mask.load(std::memory_order_acquire) != 0)
        apply_pending_resource_changer_state();

    uint64_t mask = 0;
    uint64_t unused_after_mask = 0;
    if (runtime->enabled.load(std::memory_order_acquire) == 0 ||
        !load_active_session_rule_masks(runtime, &mask, &unused_after_mask))
        return false;

    if (mask == 0)
        return false;

    bool skip_original = false;
    if ((mask & (1ULL << kRuleOpResourceOverridePlanetColorArg)) != 0) {
        if (args.color_arg0 != nullptr && resource_change_ball_color_enabled() && !resource_is_coop_mode()) {
            *args.color_arg0 = resource_planet_color();
        }
    }
    if ((mask & (1ULL << kRuleOpResourceSkipPlanetColorOriginal)) != 0) {
        const bool target_is_planet_color_path =
            args.target_kind == kTargetKindPlanetarySystemRainbowMode ||
            args.target_kind == kTargetKindPlanetarySystemEnbyMode ||
            args.target_kind == kTargetKindPlanetRendererLoadPlanetColor ||
            args.target_kind == kTargetKindPlanetRendererSetRainbow ||
            args.target_kind == kTargetKindPlanetRendererSetColor;
        if (target_is_planet_color_path &&
            resource_change_ball_color_enabled() &&
            !resource_is_coop_mode()) {
            skip_original = true;
        }
    }
    if ((mask & (1ULL << kRuleOpResourceSkipTileColorOriginal)) != 0) {
        if (resource_change_tile_color_enabled() &&
            args.target_kind == kTargetKindScrFloorSetTileColor &&
            resource_floor_is_beat(args.instance)) {
            apply_resource_floor_color(args.instance);
            skip_original = true;
        }
    }

    return skip_original;
}

void begin_shared_overlay_session(
    int32_t start_seq_id,
    bool has_start_seq,
    bool is_restart) {
    OwnerOverlayScope shared_scope(&g_legacy_overlay_session);
    starray::realtime::begin_session(starray::realtime::monotonic_now_ns());
    reset_shared_overlay_session_facts();
    {
        std::lock_guard<std::mutex> guard(g_timeline_state_lock);
        g_session_start_seq_id = start_seq_id;
        g_session_start_seq_valid = has_start_seq;
    }
    g_shared_overlay_session_visible.store(1, std::memory_order_release);
    active_overlay_state().visible.store(1, std::memory_order_release);
    active_overlay_state().last_seq_id.store(start_seq_id, std::memory_order_release);
    active_overlay_state().last_is_restart.store(is_restart ? 1u : 0u, std::memory_order_release);
    active_overlay_state().show_count.fetch_add(1, std::memory_order_relaxed);
    active_overlay_state().attempt_count.fetch_add(1, std::memory_order_relaxed);
    active_overlay_state().last_op.store(kRuleOpOverlayShow, std::memory_order_release);
    poll_overlay_telemetry(nullptr, true);
    active_overlay_state().generation.fetch_add(1, std::memory_order_release);
}

void begin_overlay_session(bool practice) {
    reset_owner_overlay_session_metrics();
    active_overlay_state().visible.store(1, std::memory_order_release);
    active_overlay_state().practice.store(practice ? 1u : 0u, std::memory_order_release);
}

void retire_shared_overlay_session() {
    OwnerOverlayScope shared_scope(&g_legacy_overlay_session);
    if (g_shared_overlay_session_visible.exchange(0, std::memory_order_acq_rel) == 0)
        return;
    // Realtime sessions are generation boundaries. Move to an empty generation
    // so completed input snapshots cannot be reused by the next gameplay attempt.
    starray::realtime::begin_session(starray::realtime::monotonic_now_ns());
    reset_shared_overlay_session_facts();
    release_resource_scene_handles();
}

bool retire_overlay_session() {
    const bool was_visible =
        active_overlay_state().visible.exchange(0, std::memory_order_acq_rel) != 0;
    active_overlay_state().practice.store(0, std::memory_order_release);
    if (!was_visible)
        return false;

    reset_owner_overlay_session_metrics();
    return true;
}

bool publish_margin_snapshot(void *tracker) {
    const int32_t percent_acc_offset = g_margin_percent_acc_offset.load(std::memory_order_acquire);
    const int32_t percent_x_acc_offset = g_margin_percent_x_acc_offset.load(std::memory_order_acquire);
    float percent_acc = 0.0f;
    float percent_x_acc = 0.0f;
    if (!read_instance_float(tracker, percent_acc_offset, percent_acc) ||
        !read_instance_float(tracker, percent_x_acc_offset, percent_x_acc)) {
        return false;
    }

    const uint32_t percent_acc_bits = float_to_bits(percent_acc);
    const uint32_t percent_x_acc_bits = float_to_bits(percent_x_acc);
    const uint32_t old_percent_acc_bits =
        active_overlay_state().percent_acc_bits.load(std::memory_order_acquire);
    const uint32_t old_percent_x_acc_bits =
        active_overlay_state().percent_x_acc_bits.load(std::memory_order_acquire);
    const uint32_t old_snapshot_count =
        active_overlay_state().accuracy_snapshot_count.load(std::memory_order_acquire);

    active_overlay_state().percent_acc_bits.store(percent_acc_bits, std::memory_order_release);
    active_overlay_state().percent_x_acc_bits.store(percent_x_acc_bits, std::memory_order_release);
    g_margin_tracker_instance.store(reinterpret_cast<uintptr_t>(tracker), std::memory_order_release);
    active_overlay_state().accuracy_snapshot_count.fetch_add(1, std::memory_order_relaxed);
    return old_snapshot_count == 0 ||
           old_percent_acc_bits != percent_acc_bits ||
           old_percent_x_acc_bits != percent_x_acc_bits;
}

void record_combo_from_margin(int32_t hit_margin) {
    constexpr int32_t kHitMarginPerfect = 3;
    constexpr int32_t kHitMarginAuto = 10;

    if (hit_margin == kHitMarginPerfect || hit_margin == kHitMarginAuto) {
        active_overlay_state().combo_count.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    active_overlay_state().combo_count.store(0, std::memory_order_release);
}

void record_bpm_snapshot(float bpm_times_speed, float conductor_pitch) {
    if (!std::isfinite(bpm_times_speed) ||
        !std::isfinite(conductor_pitch) ||
        bpm_times_speed <= 0.0f ||
        conductor_pitch <= 0.0f) {
        return;
    }

    const float tile_bpm = bpm_times_speed * conductor_pitch;
    const float kps = tile_bpm / 60.0f;
    active_overlay_state().tile_bpm_bits.store(float_to_bits(tile_bpm), std::memory_order_release);
    active_overlay_state().kps_bits.store(float_to_bits(kps), std::memory_order_release);
    active_overlay_state().bpm_snapshot_count.fetch_add(1, std::memory_order_relaxed);
}

// Owner snapshots carry local HUD state. Mirror only official game facts from
// the shared sampler so native metadata/getter work is never repeated per MOD.
bool project_shared_game_facts_to_owner() {
    auto &owner = active_overlay_state();
    auto &shared = g_legacy_overlay_session.state;
    if (&owner == &shared)
        return false;

    bool changed = false;
    const auto copy = [&changed](auto &destination, const auto &source) {
        changed |= atomic_store_if_changed(
            destination,
            source.load(std::memory_order_acquire));
    };

    copy(owner.player_count, shared.player_count);
    copy(owner.last_seq_id, shared.last_seq_id);
    copy(owner.last_is_restart, shared.last_is_restart);
    copy(owner.accuracy_snapshot_count, shared.accuracy_snapshot_count);
    copy(owner.percent_acc_bits, shared.percent_acc_bits);
    copy(owner.percent_x_acc_bits, shared.percent_x_acc_bits);
    copy(owner.progress_bits, shared.progress_bits);
    copy(owner.bpm_snapshot_count, shared.bpm_snapshot_count);
    copy(owner.tile_bpm_bits, shared.tile_bpm_bits);
    copy(owner.kps_bits, shared.kps_bits);
    copy(owner.timeline_snapshot_count, shared.timeline_snapshot_count);
    copy(owner.session_epoch, shared.session_epoch);
    copy(owner.music_time_bits, shared.music_time_bits);
    copy(owner.music_total_time_bits, shared.music_total_time_bits);
    copy(owner.map_time_bits, shared.map_time_bits);
    copy(owner.map_total_time_bits, shared.map_total_time_bits);
    copy(owner.checkpoints_used, shared.checkpoints_used);
    copy(owner.current_checkpoint, shared.current_checkpoint);
    copy(owner.total_checkpoints, shared.total_checkpoints);
    copy(owner.current_seq_id, shared.current_seq_id);
    copy(owner.floor_count, shared.floor_count);
    copy(owner.start_progress_bits, shared.start_progress_bits);
    copy(owner.speed_multiplier_bits, shared.speed_multiplier_bits);
    copy(owner.session_auto, shared.session_auto);
    copy(owner.input_state_generation, shared.input_state_generation);
    copy(owner.input_held_mask, shared.input_held_mask);
    copy(owner.input_last_down_mask, shared.input_last_down_mask);
    copy(owner.input_last_up_mask, shared.input_last_up_mask);
    copy(owner.input_total_count, shared.input_total_count);
    copy(owner.input_kps_bits, shared.input_kps_bits);
    copy(owner.planet_speed_bits, shared.planet_speed_bits);
    copy(owner.rdc_auto, shared.rdc_auto);
    copy(owner.no_fail, shared.no_fail);
    copy(owner.paused, shared.paused);
    copy(owner.is_game_world, shared.is_game_world);
    copy(owner.song_pitch_bits, shared.song_pitch_bits);
    copy(owner.conductor_add_offset_bits, shared.conductor_add_offset_bits);
    copy(owner.conductor_songposition_minusi_bits, shared.conductor_songposition_minusi_bits);
    copy(owner.is_scn_game, shared.is_scn_game);
    copy(owner.game_ready, shared.game_ready);
    return changed;
}

bool run_owner_overlay_after_ops(
    uint64_t mask,
    const FixedOpArgs &args,
    const std::vector<uint32_t> &bundle_ids) {
    mask &= kOwnerOverlayOpMask;
    if (mask == 0)
        return false;

    const uint64_t poll_mask = 1ULL << kRuleOpOverlayPollTelemetry;
    const uint64_t start_mask =
        (1ULL << kRuleOpOverlayShow) |
        (1ULL << kRuleOpOverlayShowPractice);
    const uint64_t hide_mask = 1ULL << kRuleOpOverlayHide;
    bool state_changed =
        (mask & ~(poll_mask | start_mask | hide_mask)) != 0;
    if (mask != 0)
        active_overlay_state().last_target_kind.store(args.target_kind, std::memory_order_release);

    if ((mask & (1ULL << kRuleOpOverlayShow)) != 0) {
        if (args.has_play_args) {
            active_overlay_state().last_seq_id.store(args.seq_id, std::memory_order_release);
            active_overlay_state().last_is_restart.store(args.is_restart ? 1u : 0u, std::memory_order_release);
        }
        begin_overlay_session(false);
        active_overlay_state().attempt_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().show_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayShow, std::memory_order_release);
        state_changed = true;
    }
    if ((mask & (1ULL << kRuleOpOverlayShowPractice)) != 0) {
        begin_overlay_session(true);
        active_overlay_state().attempt_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().show_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayShowPractice, std::memory_order_release);
        state_changed = true;
    }
    if ((mask & (1ULL << kRuleOpOverlayHandleStateChange)) != 0) {
        active_overlay_state().state_change_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayHandleStateChange, std::memory_order_release);
    }
    if ((mask & (1ULL << kRuleOpOverlayHide)) != 0) {
        if (args.has_wipe_direction)
            active_overlay_state().last_wipe_direction.store(args.wipe_direction, std::memory_order_release);
        if (args.has_reset_to_editor)
            active_overlay_state().last_reset_to_editor.store(args.reset_to_editor ? 1u : 0u, std::memory_order_release);
        if (retire_overlay_session()) {
            active_overlay_state().hide_count.fetch_add(1, std::memory_order_relaxed);
            active_overlay_state().last_op.store(kRuleOpOverlayHide, std::memory_order_release);
            state_changed = true;
        }
    }
    if ((mask & (1ULL << kRuleOpOverlayUpdatePlayers)) != 0) {
        if (args.has_player_count)
            active_overlay_state().player_count.store(args.player_count, std::memory_order_release);
        active_overlay_state().player_update_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayUpdatePlayers, std::memory_order_release);
    }
    const bool session_active = active_overlay_state().visible.load(std::memory_order_acquire) != 0;
    bool margin_snapshot_changed = false;
    if (session_active && (mask & (1ULL << kRuleOpPublishMarginSnapshot)) != 0) {
        margin_snapshot_changed = publish_margin_snapshot(args.instance);
        active_overlay_state().last_op.store(kRuleOpPublishMarginSnapshot, std::memory_order_release);
    }
    if (session_active && (mask & (1ULL << kRuleOpOverlayRecordHit)) != 0) {
        if (args.has_hit_margin) {
            active_overlay_state().last_hit_margin.store(args.hit_margin, std::memory_order_release);
            record_combo_from_margin(args.hit_margin);
        }
        active_overlay_state().judgement_hit_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayRecordHit, std::memory_order_release);
    }
    if (session_active && (mask & (1ULL << kRuleOpOverlayResetJudgement)) != 0) {
        active_overlay_state().judgement_reset_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_hit_margin.store(0, std::memory_order_release);
        active_overlay_state().combo_count.store(0, std::memory_order_release);
        active_overlay_state().last_op.store(kRuleOpOverlayResetJudgement, std::memory_order_release);
    }
    if (session_active && (mask & (1ULL << kRuleOpOverlayRecordFloorMove)) != 0) {
        if (args.has_floor_exit_angle)
            active_overlay_state().last_floor_exit_angle_bits.store(float_to_bits(args.floor_exit_angle), std::memory_order_release);
        if (args.has_floor_move_hit_margin)
            active_overlay_state().last_floor_move_hit_margin.store(args.floor_move_hit_margin, std::memory_order_release);
        active_overlay_state().floor_move_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayRecordFloorMove, std::memory_order_release);
    }
    if (session_active && (mask & (1ULL << kRuleOpOverlayRecordPlayerHit)) != 0) {
        if (args.has_player_hit_is_auto)
            active_overlay_state().last_player_hit_is_auto.store(args.player_hit_is_auto ? 1u : 0u, std::memory_order_release);
        active_overlay_state().player_hit_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayRecordPlayerHit, std::memory_order_release);
    }
    if (session_active && (mask & (1ULL << kRuleOpOverlayRecordDeath)) != 0) {
        if (args.has_death_args) {
            active_overlay_state().last_death_overload.store(args.death_overload ? 1u : 0u, std::memory_order_release);
            active_overlay_state().last_death_multipress.store(args.death_multipress ? 1u : 0u, std::memory_order_release);
            active_overlay_state().last_death_hitbox.store(args.death_hitbox ? 1u : 0u, std::memory_order_release);
        }
        active_overlay_state().death_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayRecordDeath, std::memory_order_release);
    }
    if (session_active && (mask & (1ULL << kRuleOpOverlayRecordHitTiming)) != 0) {
        if (args.has_hit_timing) {
            active_overlay_state().last_hit_timing_ms_bits.store(float_to_bits(args.hit_timing_ms), std::memory_order_release);
            active_overlay_state().last_hit_timing_margin.store(args.hit_timing_margin, std::memory_order_release);
            record_bpm_snapshot(args.bpm_times_speed, args.conductor_pitch);
        }
        active_overlay_state().hit_timing_count.fetch_add(1, std::memory_order_relaxed);
        active_overlay_state().last_op.store(kRuleOpOverlayRecordHitTiming, std::memory_order_release);
    }
    if ((mask & (poll_mask | start_mask)) != 0) {
        if (project_shared_game_facts_to_owner()) {
            state_changed = true;
            active_overlay_state().last_op.store(kRuleOpOverlayPollTelemetry, std::memory_order_release);
        }
    }
    if (!state_changed)
        return false;

    const uint32_t generation =
        active_overlay_state().generation.fetch_add(1, std::memory_order_acq_rel) + 1;

    const uint64_t visibility_mask =
        (1ULL << kRuleOpOverlayShow) |
        (1ULL << kRuleOpOverlayShowPractice) |
        (1ULL << kRuleOpOverlayHide);
    if ((mask & visibility_mask) != 0) {
        const bool visible =
            active_overlay_state().visible.load(std::memory_order_acquire) != 0;
        if (bundle_ids.empty()) {
            starray::ui_recipe_runtime::publish_overlay_state(generation, visible);
        } else {
            for (const uint32_t bundle_id : bundle_ids) {
                starray::ui_recipe_runtime::publish_bundle_overlay_state(
                    bundle_id,
                    generation,
                    visible);
            }
        }
    }

    // GetHitMargin/CalculatePercentAcc/MoveToNextFloor can all run during one
    // judgement. Notify managed UI only after stable lifecycle/final-result
    // operations so one input does not rebuild the same HUD several times.
    uint64_t notify_mask = mask & kUnityHudStablePointMask;
    if ((notify_mask & (1ULL << kRuleOpPublishMarginSnapshot)) != 0) {
        const uint64_t non_margin_notify_mask =
            notify_mask & ~(1ULL << kRuleOpPublishMarginSnapshot);
        if (non_margin_notify_mask == 0) {
            notify_mask = 0;
            if (margin_snapshot_changed) {
                const int64_t now_ms = steady_time_ms();
                const int64_t last_ms =
                    active_owner_overlay_session().last_margin_callback_ms.load(
                        std::memory_order_acquire);
                if (last_ms == 0 ||
                    now_ms - last_ms >= kMarginSnapshotCallbackIntervalMs) {
                    active_owner_overlay_session().last_margin_callback_ms.store(
                        now_ms,
                        std::memory_order_release);
                    notify_mask = (1ULL << kRuleOpPublishMarginSnapshot);
                }
            }
        }
    }

    if (notify_mask != 0) {
        const auto callback = g_overlay_changed_callback.load(std::memory_order_acquire);
        if (callback != nullptr)
            callback(generation);
    }

    return true;
}

void run_shared_after_ops(
    int dispatcher_index,
    uint64_t mask,
    const FixedOpArgs &args) {
    (void)dispatcher_index;
    OwnerOverlayScope shared_scope(&g_legacy_overlay_session);
    bool shared_changed = false;
    const uint64_t start_mask =
        (1ULL << kRuleOpOverlayShow) |
        (1ULL << kRuleOpOverlayShowPractice);
    if ((mask & start_mask) != 0)
        begin_shared_overlay_session(
            args.seq_id,
            args.has_play_args,
            args.is_restart);
    if ((mask & (1ULL << kRuleOpOverlayHide)) != 0)
        retire_shared_overlay_session();

    if ((mask & (1ULL << kRuleOpOverlayUpdatePlayers)) != 0) {
        if (args.has_player_count) {
            shared_changed |= atomic_store_if_changed(
                active_overlay_state().player_count,
                args.player_count);
        }
        active_overlay_state().player_update_count.fetch_add(1, std::memory_order_relaxed);
    }

    if ((mask & ((1ULL << kRuleOpOverlayShow) |
                 (1ULL << kRuleOpOverlayShowPractice) |
                 (1ULL << kRuleOpOverlayUpdatePlayers))) != 0 ||
        (args.instance != nullptr &&
         (mask & ((1ULL << kRuleOpOverlayRecordHit) |
                  (1ULL << kRuleOpOverlayResetJudgement) |
                  (1ULL << kRuleOpPublishMarginSnapshot))) != 0)) {
        const bool tracker_method =
            args.target_kind == kTargetKindMarginTrackerAddHit ||
            args.target_kind == kTargetKindMarginTrackerReset ||
            args.target_kind == kTargetKindMarginTrackerCalculatePercentAcc;
        if (publish_hit_margin_snapshot(tracker_method ? args.instance : nullptr)) {
            g_last_hit_margin_authoritative_publish_ms.store(
                steady_time_ms(),
                std::memory_order_release);
        }
    }

    if ((mask & (1ULL << kRuleOpPublishMarginSnapshot)) != 0 &&
        args.instance != nullptr) {
        shared_changed |= publish_margin_snapshot(args.instance);
    }

    if ((mask & (1ULL << kRuleOpOverlayRecordFloorMove)) != 0) {
        const uint32_t before = active_overlay_state().progress_bits.load(
            std::memory_order_acquire);
        publish_controller_progress_snapshot();
        shared_changed |= before != active_overlay_state().progress_bits.load(
            std::memory_order_acquire);
    }

    if ((mask & (1ULL << kRuleOpOverlayPollTelemetry)) != 0 &&
        poll_overlay_telemetry(args.instance, false)) {
        active_overlay_state().last_op.store(kRuleOpOverlayPollTelemetry, std::memory_order_release);
        shared_changed = true;
    }

    if ((mask & (1ULL << kRuleOpResourceApplyEditorRabbit)) != 0)
        apply_resource_editor_rabbit(args.instance);
    if ((mask & (1ULL << kRuleOpResourceApplyFloorColor)) != 0)
        apply_resource_floor_color(args.instance);
    if ((mask & (1ULL << kRuleOpResourceApplyPlanetColor)) != 0)
        apply_resource_planet_color(args.instance);
    if ((mask & (1ULL << kRuleOpResourceApplyLogoText)) != 0)
        apply_resource_logo_text(args.instance);

    if ((mask & (1ULL << kRuleOpGameplayAcceptedObserve)) != 0 &&
        args.has_bool_result &&
        args.bool_result &&
        args.has_gameplay_input_args) {
        starray::realtime::observe_gameplay_accepted(
            args.gameplay_input_is_auto,
            args.gameplay_input_state,
            starray::realtime::monotonic_now_ns(),
            starray::async_input_bridge::test_macro_enabled());
    }

    if (shared_changed)
        active_overlay_state().generation.fetch_add(1, std::memory_order_acq_rel);

}

void enqueue_managed_after_owner_ops(
    int dispatcher_index,
    uint64_t mask,
    const FixedOpArgs &args) {
    if ((mask & (1ULL << kRuleOpManagedEventCallback)) != 0)
        enqueue_managed_event_rules(dispatcher_index, args);
}

void run_fixed_after_ops(int dispatcher_index, const FixedOpArgs &args) {
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    if (runtime == nullptr)
        return;

    if (g_resource_pending_restore_mask.load(std::memory_order_acquire) != 0)
        apply_pending_resource_changer_state();
    uint64_t unused_before_mask = 0;
    uint64_t mask = 0;
    if (runtime->enabled.load(std::memory_order_acquire) == 0 ||
        !load_active_session_rule_masks(runtime, &unused_before_mask, &mask))
        return;

    if (mask == 0)
        return;

    const uint32_t call_count =
        runtime->call_count.fetch_add(1, std::memory_order_relaxed) + 1;
    run_shared_after_ops(dispatcher_index, mask, args);

    const auto subscribers = std::atomic_load_explicit(
        &runtime->owner_overlay_after_rules,
        std::memory_order_acquire);
    size_t owner_count = 0;
    uint32_t visible_count = 0;
    if (subscribers != nullptr && !subscribers->empty()) {
        for (const auto &target : *subscribers) {
            const auto &session = target.session;
            if (!pc_mod_session_token_active(target.pc_mod_session) ||
                session == nullptr ||
                session->retired.load(std::memory_order_acquire) != 0) {
                continue;
            }
            OwnerOverlayScope scope(session.get());
            run_owner_overlay_after_ops(target.after_op_mask, args, target.bundle_ids);
            ++owner_count;
            visible_count += active_overlay_state().visible.load(std::memory_order_relaxed) != 0
                ? 1u
                : 0u;
        }
    }
    else if ((mask & kOwnerOverlayOpMask) != 0) {
        static const std::vector<uint32_t> no_bundles;
        run_owner_overlay_after_ops(mask, args, no_bundles);
        owner_count = 1;
        visible_count = active_overlay_state().visible.load(std::memory_order_relaxed) != 0
            ? 1u
            : 0u;
    }

    // Managed callbacks run once after every owner reducer has committed.
    enqueue_managed_after_owner_ops(dispatcher_index, mask, args);

    if (call_count <= 3 || (call_count % 1024u) == 0) {
        LOGI("dispatcher slot=%u index=%d calls=%u beforeMask=0x%llx afterMask=0x%llx overlayOwners=%zu visibleOwners=%u",
             runtime->slot_id.load(std::memory_order_relaxed),
             dispatcher_index,
             call_count,
             static_cast<unsigned long long>(runtime->before_op_mask.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(mask),
             owner_count,
             visible_count);
    }
}

void report_missing_original(int dispatcher_index) {
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    if (runtime == nullptr)
        return;

    const uint32_t faults = runtime->fault_count.fetch_add(1, std::memory_order_relaxed) + 1;
    if (faults <= 3) {
        LOGE("dispatcher slot=%u index=%d has no original trampoline",
             runtime->slot_id.load(std::memory_order_relaxed),
             dispatcher_index);
    }
}

void *load_original_with_install_spin(int dispatcher_index) {
    // The broker activates the detour before the installing thread re-acquires
    // g_lock and publishes the continuation. Calls landing in that window would
    // otherwise be swallowed (void targets) or return fabricated values
    // (bool/int targets). Spin briefly; in steady state this exits on the
    // first iteration because disable paths preserve the original trampoline.
    auto *runtime = dispatcher_runtime_slot(dispatcher_index);
    if (runtime == nullptr)
        return nullptr;
    constexpr int kMaxInstallSpinIterations = 20000;
    for (int attempt = 0; attempt < kMaxInstallSpinIterations; ++attempt) {
        void *original = runtime->original.load(std::memory_order_acquire);
        if (original != nullptr)
            return original;
        if (attempt % 64 == 63)
            std::this_thread::yield();
    }
    return nullptr;
}

void dispatcher_instance_void0(int dispatcher_index, void *self, void *method_info) {
    auto original = reinterpret_cast<InstanceVoid0Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

void dispatcher_instance_void1(int dispatcher_index, void *self, void *arg0, void *method_info) {
    auto original = reinterpret_cast<InstanceVoid1Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_pointer(args, 0, arg0);
    const auto raw_arg0 = static_cast<int32_t>(reinterpret_cast<uintptr_t>(arg0));
    if (args.target_kind == kTargetKindControllerStartLoadingScene) {
        args.has_wipe_direction = true;
        args.wipe_direction = raw_arg0;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[0]));
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

void dispatcher_instance_void_int1(int dispatcher_index, void *self, int arg0, void *method_info) {
    auto original = reinterpret_cast<InstanceVoidInt1Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_arg(args, 0, static_cast<uint32_t>(arg0));
    if (args.target_kind == kTargetKindEditorResetScene ||
        args.target_kind == kTargetKindEditorSwitchToEditMode) {
        args.has_reset_to_editor = true;
        args.reset_to_editor = arg0 != 0;
    } else if (args.target_kind == kTargetKindMarginTrackerAddHit) {
        args.has_hit_margin = true;
        args.hit_margin = arg0;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = raw_i32_argument(args, 0);
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

void dispatcher_instance_void_ptr_float_int(int dispatcher_index, void *self, void *arg0, float arg1, int arg2, void *method_info) {
    auto original = reinterpret_cast<InstanceVoidPtrFloatIntFn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, arg1, arg2, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_pointer(args, 0, arg0);
    capture_raw_float(args, 1, arg1);
    capture_raw_arg(args, 2, static_cast<uint32_t>(arg2));
    if (args.target_kind == kTargetKindPlanetMoveToNextFloor) {
        args.has_floor_exit_angle = true;
        args.floor_exit_angle = arg1;
        args.has_floor_move_hit_margin = true;
        args.floor_move_hit_margin = arg2;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[0]));
    arg1 = raw_float_argument(args, 1);
    arg2 = raw_i32_argument(args, 2);
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, arg1, arg2, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

void dispatcher_instance_void3(int dispatcher_index, void *self, void *arg0, void *arg1, void *arg2, void *method_info) {
    auto original = reinterpret_cast<InstanceVoid3Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, arg1, arg2, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_pointer(args, 0, arg0);
    capture_raw_pointer(args, 1, arg1);
    capture_raw_pointer(args, 2, arg2);
    if (args.target_kind == kTargetKindUiControllerWipeToBlack) {
        args.has_wipe_direction = true;
        args.wipe_direction = static_cast<int32_t>(reinterpret_cast<uintptr_t>(arg0));
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[0]));
    arg1 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[1]));
    arg2 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[2]));
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, arg1, arg2, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

void dispatcher_instance_void_bool_bool_ptr_bool(int dispatcher_index, void *self, bool arg0, bool arg1, void *arg2, bool arg3, void *method_info) {
    auto original = reinterpret_cast<InstanceVoidBoolBoolPtrBoolFn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, arg1, arg2, arg3, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_arg(args, 0, arg0 ? 1u : 0u);
    capture_raw_arg(args, 1, arg1 ? 1u : 0u);
    capture_raw_pointer(args, 2, arg2);
    capture_raw_arg(args, 3, arg3 ? 1u : 0u);
    if (args.target_kind == kTargetKindPlayerDie) {
        args.has_death_args = true;
        args.death_overload = arg0;
        args.death_multipress = arg1;
        args.death_hitbox = arg3;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = raw_bool_argument(args, 0);
    arg1 = raw_bool_argument(args, 1);
    arg2 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[2]));
    arg3 = raw_bool_argument(args, 3);
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, arg1, arg2, arg3, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

bool dispatcher_instance_bool1(int dispatcher_index, void *self, bool arg0, void *method_info) {
    auto original = reinterpret_cast<InstanceBool1Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return false;
    }
    if (!pccompat_runtime_enabled(0)) {
        return original(self, arg0, method_info);
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_arg(args, 0, arg0 ? 1u : 0u);
    if (args.target_kind == kTargetKindPlayerHit) {
        args.has_player_hit_is_auto = true;
        args.player_hit_is_auto = arg0;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    args.managed_result_kind = 1;
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = raw_bool_argument(args, 0);
    const bool skip = managed_skip || fixed_skip;
    const bool result = skip
        ? args.managed_result_value != 0
        : original(self, arg0, method_info);
    args.has_bool_result = true;
    args.bool_result = result;
    args.managed_result_value = result ? 1u : 0u;
    args.managed_result_valid = 1;
    run_fixed_after_ops(dispatcher_index, args);
    return result;
}

bool dispatcher_instance_bool2(int dispatcher_index, void *self, int arg0, bool arg1, void *method_info) {
    auto original = reinterpret_cast<InstanceBool2Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return false;
    }
    if (!pccompat_runtime_enabled(0)) {
        return original(self, arg0, arg1, method_info);
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_arg(args, 0, static_cast<uint32_t>(arg0));
    capture_raw_arg(args, 1, arg1 ? 1u : 0u);
    if (args.target_kind == kTargetKindScnGamePlay) {
        args.has_play_args = true;
        args.seq_id = arg0;
        args.is_restart = arg1;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    args.managed_result_kind = 1;
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = raw_i32_argument(args, 0);
    arg1 = raw_bool_argument(args, 1);
    const bool skip = managed_skip || fixed_skip;
    const bool result = skip
        ? args.managed_result_value != 0
        : original(self, arg0, arg1, method_info);
    args.has_bool_result = true;
    args.bool_result = result;
    args.managed_result_value = result ? 1u : 0u;
    args.managed_result_valid = 1;
    run_fixed_after_ops(dispatcher_index, args);
    return result;
}

bool dispatcher_instance_bool_bool_int(int dispatcher_index,
                                       void *self,
                                       bool arg0,
                                       int arg1,
                                       void *method_info) {
    auto original = reinterpret_cast<InstanceBoolBoolIntFn>(
        load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return false;
    }
    if (!pccompat_runtime_enabled(0)) {
        return original(self, arg0, arg1, method_info);
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_arg(args, 0, arg0 ? 1u : 0u);
    capture_raw_arg(args, 1, static_cast<uint32_t>(arg1));
    if (args.target_kind == kTargetKindPlayerHitInputEvent) {
        args.has_gameplay_input_args = true;
        args.gameplay_input_is_auto = arg0;
        args.gameplay_input_state = arg1;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{
            .target_kind = dispatcher_target_kind(dispatcher_index),
            .instance = self,
        });
    args.managed_result_kind = 1;
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = raw_bool_argument(args, 0);
    arg1 = raw_i32_argument(args, 1);
    const bool skip = managed_skip || fixed_skip;
    const bool result = skip
        ? args.managed_result_value != 0
        : original(self, arg0, arg1, method_info);
    args.has_bool_result = true;
    args.bool_result = result;
    args.managed_result_value = result ? 1u : 0u;
    args.managed_result_valid = 1;
    run_fixed_after_ops(dispatcher_index, args);
    return result;
}

void dispatcher_static_void1(int dispatcher_index, int arg0, void *method_info) {
    auto original = reinterpret_cast<StaticVoid1Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(arg0, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index);
    capture_raw_arg(args, 0, static_cast<uint32_t>(arg0));
    if (args.target_kind == kTargetKindMistakesSetPlayerCount) {
        args.has_player_count = true;
        args.player_count = arg0;
    }
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index)});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    arg0 = raw_i32_argument(args, 0);
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(arg0, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

int dispatcher_static_int_float_float_bool_float_float_double(
    int dispatcher_index,
    float hitangle,
    float refangle,
    bool is_cw,
    float bpm_times_speed,
    float conductor_pitch,
    double margin_scale,
    void *method_info) {
    auto original = reinterpret_cast<StaticIntFloatFloatBoolFloatFloatDoubleFn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return 0;
    }
    if (!pccompat_runtime_enabled(0)) {
        return original(hitangle, refangle, is_cw, bpm_times_speed,
                        conductor_pitch, margin_scale, method_info);
    }

    auto args = make_fixed_op_args(dispatcher_index);
    capture_raw_float(args, 0, hitangle);
    capture_raw_float(args, 1, refangle);
    capture_raw_arg(args, 2, is_cw ? 1u : 0u);
    capture_raw_float(args, 3, bpm_times_speed);
    capture_raw_float(args, 4, conductor_pitch);
    capture_raw_double(args, 5, margin_scale);
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index)});
    args.managed_result_kind = 2;
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    hitangle = raw_float_argument(args, 0);
    refangle = raw_float_argument(args, 1);
    is_cw = raw_bool_argument(args, 2);
    bpm_times_speed = raw_float_argument(args, 3);
    conductor_pitch = raw_float_argument(args, 4);
    uint64_t margin_bits = args.raw_args[5];
    std::memcpy(&margin_scale, &margin_bits, sizeof(margin_scale));
    const bool skip = managed_skip || fixed_skip;
    const int result = skip
        ? static_cast<int32_t>(args.managed_result_value)
        : original(hitangle, refangle, is_cw, bpm_times_speed, conductor_pitch, margin_scale, method_info);
    args.managed_result_value = static_cast<uint32_t>(result);
    args.managed_result_valid = 1;
    if (args.target_kind == kTargetKindMiscGetHitMargin) {
        args.has_hit_timing = true;
        args.bpm_times_speed = bpm_times_speed;
        args.conductor_pitch = conductor_pitch;
        if (bpm_times_speed != 0.0f && conductor_pitch != 0.0f) {
            const float signed_angle = (hitangle - refangle) * (is_cw ? 1.0f : -1.0f) * 57.29578f;
            args.hit_timing_ms = signed_angle / 180.0f / bpm_times_speed / conductor_pitch * 60000.0f;
        } else {
            args.hit_timing_ms = 0.0f;
        }
        args.hit_timing_margin = result;
    }
    run_fixed_after_ops(dispatcher_index, args);
    return result;
}

void dispatcher_instance_void_color1(int dispatcher_index, void *self, ColorValue arg0, void *method_info) {
    auto original = reinterpret_cast<InstanceVoidColor1Fn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, method_info);
        return;
    }

    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{
            .target_kind = dispatcher_target_kind(dispatcher_index),
            .instance = self,
            .color_arg0 = &arg0});
    auto color_args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_float(color_args, 0, arg0.r);
    capture_raw_float(color_args, 1, arg0.g);
    capture_raw_float(color_args, 2, arg0.b);
    capture_raw_float(color_args, 3, arg0.a);
    const bool skip = run_managed_prefix_rules(dispatcher_index, color_args) || fixed_skip;
    self = color_args.instance;
    if (!skip)
        original(self, arg0, method_info);
    run_fixed_after_ops(dispatcher_index, color_args);
}

void dispatcher_instance_void_int_bool(int dispatcher_index, void *self, int arg0, bool arg1, void *method_info) {
    auto original = reinterpret_cast<InstanceVoidIntBoolFn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, arg1, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_arg(args, 0, static_cast<uint32_t>(arg0));
    capture_raw_arg(args, 1, arg1 ? 1u : 0u);
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{.target_kind = dispatcher_target_kind(dispatcher_index), .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = raw_i32_argument(args, 0);
    arg1 = raw_bool_argument(args, 1);
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, arg1, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}

void dispatcher_instance_void_ptr_bool(int dispatcher_index, void *self, void *arg0, bool arg1, void *method_info) {
    auto original = reinterpret_cast<InstanceVoidPtrBoolFn>(load_original_with_install_spin(dispatcher_index));
    if (original == nullptr) {
        report_missing_original(dispatcher_index);
        return;
    }
    if (!pccompat_runtime_enabled(0)) {
        original(self, arg0, arg1, method_info);
        return;
    }

    auto args = make_fixed_op_args(dispatcher_index, self);
    capture_raw_pointer(args, 0, arg0);
    capture_raw_arg(args, 1, arg1 ? 1u : 0u);
    const bool fixed_skip = run_fixed_before_ops(
        dispatcher_index,
        FixedBeforeArgs{
            .target_kind = dispatcher_target_kind(dispatcher_index),
            .instance = self});
    const bool managed_skip = run_managed_prefix_rules(dispatcher_index, args);
    refresh_fixed_args_after_managed_prefix(args);
    self = args.instance;
    arg0 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[0]));
    arg1 = raw_bool_argument(args, 1);
    const bool skip = managed_skip || fixed_skip;
    if (!skip)
        original(self, arg0, arg1, method_info);
    run_fixed_after_ops(dispatcher_index, args);
}



constexpr size_t kDispatcherThunkStride = 64;

struct DispatcherThunkSpec {
    void *target = nullptr;
    uint8_t gp_argument_count = 0;
};

struct DispatcherThunkPage {
    void *mapping = nullptr;
    size_t mapping_size = 0;
    int first_index = 0;
    int count = 0;
    DispatcherThunkPage *next = nullptr;
};

DispatcherThunkPage *g_dispatcher_thunk_head = nullptr;
DispatcherThunkPage *g_dispatcher_thunk_tail = nullptr;

DispatcherThunkSpec dispatcher_thunk_spec(const std::string &abi_kind) {
    void *target = nullptr;
    if (abi_kind == "InstanceVoid0")
        target = reinterpret_cast<void *>(&dispatcher_instance_void0);
    else if (abi_kind == "InstanceVoid1")
        target = reinterpret_cast<void *>(&dispatcher_instance_void1);
    else if (abi_kind == "InstanceVoidInt1")
        target = reinterpret_cast<void *>(&dispatcher_instance_void_int1);
    else if (abi_kind == "InstanceVoidPtrFloatInt")
        target = reinterpret_cast<void *>(&dispatcher_instance_void_ptr_float_int);
    else if (abi_kind == "InstanceVoid3")
        target = reinterpret_cast<void *>(&dispatcher_instance_void3);
    else if (abi_kind == "InstanceVoidBoolBoolPtrBool")
        target = reinterpret_cast<void *>(&dispatcher_instance_void_bool_bool_ptr_bool);
    else if (abi_kind == "InstanceVoidColor1")
        target = reinterpret_cast<void *>(&dispatcher_instance_void_color1);
    else if (abi_kind == "InstanceVoidIntBool")
        target = reinterpret_cast<void *>(&dispatcher_instance_void_int_bool);
    else if (abi_kind == "InstanceVoidPtrBool")
        target = reinterpret_cast<void *>(&dispatcher_instance_void_ptr_bool);
    else if (abi_kind == "InstanceBool1")
        target = reinterpret_cast<void *>(&dispatcher_instance_bool1);
    else if (abi_kind == "InstanceBool2")
        target = reinterpret_cast<void *>(&dispatcher_instance_bool2);
    else if (abi_kind == "InstanceBoolBoolInt")
        target = reinterpret_cast<void *>(&dispatcher_instance_bool_bool_int);
    else if (abi_kind == "StaticVoid1")
        target = reinterpret_cast<void *>(&dispatcher_static_void1);
    else if (abi_kind == "StaticIntFloatFloatBoolFloatFloatDouble")
        target = reinterpret_cast<void *>(&dispatcher_static_int_float_float_bool_float_float_double);

    DispatcherAbiSpec abi_spec;
    if (target == nullptr || !get_dispatcher_abi_spec(abi_kind, abi_spec))
        return {};

    size_t gp_argument_count = abi_spec.is_static ? 1u : 2u;  // hidden MethodInfo* plus optional this
    for (const auto value_class : abi_spec.params) {
        if (value_class != AbiValueClass::Float32 &&
            value_class != AbiValueClass::Float64 &&
            value_class != AbiValueClass::ColorValue) {
            ++gp_argument_count;
        }
    }
    if (gp_argument_count >= 8)
        return {};
    return {target, static_cast<uint8_t>(gp_argument_count)};
}

bool write_aarch64_dispatcher_thunk(void *entry,
                                    int dispatcher_index,
                                    const DispatcherThunkSpec &spec,
                                    std::string &error) {
#if defined(__aarch64__)
    if (entry == nullptr || dispatcher_index < 0 || spec.target == nullptr ||
        spec.gp_argument_count >= 8) {
        error = "invalid AArch64 dispatcher thunk request";
        return false;
    }

    auto *words = static_cast<uint32_t *>(entry);
    constexpr size_t word_capacity = kDispatcherThunkStride / sizeof(uint32_t);
    std::fill(words, words + word_capacity, 0xd503201fu);  // nop
    size_t cursor = 0;
    auto emit = [&](uint32_t instruction) {
        words[cursor++] = instruction;
    };

    emit(0xd503245fu);  // bti c
    for (int source = static_cast<int>(spec.gp_argument_count) - 1; source >= 0; --source) {
        const uint32_t destination = static_cast<uint32_t>(source + 1);
        emit(0xaa0003e0u | (static_cast<uint32_t>(source) << 16u) | destination);
    }
    const uint32_t index = static_cast<uint32_t>(dispatcher_index);
    emit(0x52800000u | ((index & 0xffffu) << 5u));  // movz w0, low16
    if ((index >> 16u) != 0)
        emit(0x72a00000u | (((index >> 16u) & 0xffffu) << 5u));  // movk w0, high16, lsl 16

    const size_t ldr_index = cursor++;
    emit(0xd61f0200u);  // br x16
    if ((cursor & 1u) != 0)
        emit(0xd503201fu);
    const size_t literal_index = cursor;
    const uint64_t target = reinterpret_cast<uint64_t>(spec.target);
    if (literal_index + 2 > word_capacity) {
        error = "AArch64 dispatcher thunk exceeds fixed stride";
        return false;
    }
    std::memcpy(words + literal_index, &target, sizeof(target));
    const uint32_t literal_distance = static_cast<uint32_t>(literal_index - ldr_index);
    words[ldr_index] = 0x58000000u | (literal_distance << 5u) | 16u;  // ldr x16, literal
    return true;
#else
    (void)entry;
    (void)dispatcher_index;
    (void)spec;
    error = "dynamic dispatcher thunk arena requires AArch64";
    return false;
#endif
}

bool allocate_dispatcher_batch_locked(const std::vector<HookSlot *> &new_slots,
                                      std::string &error) {
    if (new_slots.empty())
        return true;
    if (new_slots.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
        error = "dispatcher batch exceeds index range";
        return false;
    }

    const int first_index = g_dispatcher_capacity.load(std::memory_order_acquire);
    const int new_dispatchers = static_cast<int>(new_slots.size());
    if (first_index > std::numeric_limits<int>::max() - new_dispatchers) {
        error = "dispatcher arena index overflow";
        return false;
    }

    auto runtime_page = std::unique_ptr<DispatcherRuntimePage>(
        new (std::nothrow) DispatcherRuntimePage(first_index, new_dispatchers));
    if (!runtime_page || !runtime_page->slots) {
        error = "dispatcher runtime page allocation failed";
        return false;
    }

    const long page_size_value = sysconf(_SC_PAGESIZE);
    const size_t page_size = page_size_value > 0
        ? static_cast<size_t>(page_size_value)
        : static_cast<size_t>(4096);
    const size_t requested_bytes = new_slots.size() * kDispatcherThunkStride;
    const size_t mapping_size = ((requested_bytes + page_size - 1) / page_size) * page_size;
    void *mapping = mmap(
        nullptr,
        mapping_size,
        PROT_READ | PROT_WRITE,
        MAP_PRIVATE | MAP_ANONYMOUS,
        -1,
        0);
    if (mapping == MAP_FAILED) {
        error = "dispatcher thunk mmap failed errno=" + std::to_string(errno);
        return false;
    }

    bool generated = true;
    for (int offset = 0; offset < new_dispatchers; ++offset) {
        auto *slot = new_slots[static_cast<size_t>(offset)];
        const auto spec = slot != nullptr ? dispatcher_thunk_spec(slot->abi_kind) : DispatcherThunkSpec{};
        void *entry = static_cast<uint8_t *>(mapping) +
                      static_cast<size_t>(offset) * kDispatcherThunkStride;
        if (slot == nullptr || !write_aarch64_dispatcher_thunk(
                entry,
                first_index + offset,
                spec,
                error)) {
            generated = false;
            break;
        }
        auto &runtime = runtime_page->slots[static_cast<size_t>(offset)];
        runtime.detour_entry = entry;
        runtime.allocated_abi_kind = slot->abi_kind;
    }
    if (!generated) {
        munmap(mapping, mapping_size);
        return false;
    }

    __builtin___clear_cache(
        static_cast<char *>(mapping),
        static_cast<char *>(mapping) + requested_bytes);
    if (mprotect(mapping, mapping_size, PROT_READ | PROT_EXEC) != 0) {
        error = "dispatcher thunk mprotect RX failed errno=" + std::to_string(errno);
        munmap(mapping, mapping_size);
        return false;
    }

    auto thunk_page = std::unique_ptr<DispatcherThunkPage>(
        new (std::nothrow) DispatcherThunkPage{});
    if (!thunk_page) {
        error = "dispatcher thunk page metadata allocation failed";
        munmap(mapping, mapping_size);
        return false;
    }
    thunk_page->mapping = mapping;
    thunk_page->mapping_size = mapping_size;
    thunk_page->first_index = first_index;
    thunk_page->count = new_dispatchers;

    auto *published_runtime_page = runtime_page.release();
    if (g_dispatcher_runtime_tail == nullptr) {
        g_dispatcher_runtime_head.store(published_runtime_page, std::memory_order_release);
    } else {
        g_dispatcher_runtime_tail->next.store(published_runtime_page, std::memory_order_release);
    }
    g_dispatcher_runtime_tail = published_runtime_page;

    auto *published_thunk_page = thunk_page.release();
    if (g_dispatcher_thunk_tail == nullptr)
        g_dispatcher_thunk_head = published_thunk_page;
    else
        g_dispatcher_thunk_tail->next = published_thunk_page;
    g_dispatcher_thunk_tail = published_thunk_page;
    g_dispatcher_capacity.store(first_index + new_dispatchers, std::memory_order_release);

    for (int offset = 0; offset < new_dispatchers; ++offset)
        new_slots[static_cast<size_t>(offset)]->dispatcher_index = first_index + offset;
    return true;
}

void *dispatcher_detour_for_slot(const HookSlot &slot) {
    auto *runtime = dispatcher_runtime_slot(slot.dispatcher_index);
    if (runtime == nullptr || runtime->allocated_abi_kind != slot.abi_kind)
        return nullptr;
    return runtime->detour_entry;
}

bool is_resource_set_rainbow_composite_slot(const HookSlot &slot) {
    return slot.assembly_name == "Assembly-CSharp" &&
           slot.namespace_name.empty() &&
           slot.type_name == "PlanetRenderer" &&
           slot.method_name == "SetRainbow" &&
           !slot.is_static &&
           slot.return_type == "System.Void" &&
           slot.parameter_types.size() == 1 &&
           slot.parameter_types[0] == "System.Boolean";
}

bool enabled_rules_are_resource_planet_color_skip(const HookSlot &slot) {
    bool found = false;
    const auto check = [&found](const std::vector<HookSlotRuleRef> &rules) {
        for (const auto &rule : rules) {
            if (!rule.enabled)
                continue;
            found = true;
            if (rule.op_code != kRuleOpResourceSkipPlanetColorOriginal)
                return false;
        }
        return true;
    };
    return check(slot.before_rules) &&
           check(slot.replace_rules) &&
           check(slot.after_rules) &&
           found;
}

int prepare_install_plan_locked() {
    std::vector<HookSlot *> installable_staged_slots;
    for (auto &slot : g_state.slots) {
        slot.install_planned = false;
        slot.install_blocked = false;

        if (slot.state == SlotSkippedKnownConflict)
            slot.state = SlotResolved;

        if (slot.state == SlotHookInstalled)
            continue;

        if (slot.state != SlotResolved) {
            if (slot.state == SlotPendingResolve)
                slot.status = slot.resolve_failed ? slot.status : "pending metadata resolve";
            continue;
        }

        if (slot_enabled_rule_count(slot) <= 0) {
            slot.state = SlotDisabledByCapability;
            slot.status = "all rules disabled";
            continue;
        }

        if (is_resource_set_rainbow_composite_slot(slot) &&
            enabled_rules_are_resource_planet_color_skip(slot)) {
            slot.state = SlotSkippedKnownConflict;
            slot.status = "short setter covered by composite ResourceSkipPlanetColorOriginal targets";
            continue;
        }

        if (enabled_rule_count(slot.replace_rules) > 0) {
            slot.install_blocked = true;
            slot.status = "replace rules are not supported by the first native dispatcher";
            continue;
        }

        if (!is_first_dispatcher_abi_supported(slot)) {
            slot.install_blocked = true;
            slot.status = "unsupported first dispatcher abi: " + slot.abi_kind;
            continue;
        }

        std::string unsupported_reason;
        if (has_unsupported_first_dispatcher_op(slot, unsupported_reason)) {
            slot.install_blocked = true;
            slot.status = unsupported_reason;
            continue;
        }

        installable_staged_slots.push_back(&slot);
    }

    const int bound_dispatchers = count_bound_dispatchers_locked();
    const int new_dispatchers = static_cast<int>(installable_staged_slots.size());
    const int required_dispatchers = bound_dispatchers + new_dispatchers;
    g_state.dispatcher_required = required_dispatchers;
    g_state.dispatcher_new = new_dispatchers;

    std::vector<HookSlot *> allocation_batch;
    allocation_batch.reserve(installable_staged_slots.size());
    for (auto *slot : installable_staged_slots) {
        if (slot->dispatcher_index < 0)
            allocation_batch.push_back(slot);
    }

    std::string allocation_error;
    if (!allocate_dispatcher_batch_locked(allocation_batch, allocation_error)) {
        for (auto *slot : installable_staged_slots) {
            slot->install_planned = false;
            slot->install_blocked = true;
            slot->status = "dispatcher batch allocation failed: " + allocation_error;
        }
        g_state.dispatcher_allocated = 0;
        g_state.dispatcher_remaining = std::max(
            0,
            g_dispatcher_capacity.load(std::memory_order_acquire) - required_dispatchers);
        g_state.dispatcher_blocked = static_cast<int>(installable_staged_slots.size());
        if (g_state.last_error.empty())
            g_state.last_error = allocation_error;
        return 0;
    }

    int planned = 0;
    int allocated = 0;
    for (auto *slot : installable_staged_slots) {
        if (dispatcher_detour_for_slot(*slot) == nullptr) {
            slot->install_blocked = true;
            slot->status = "dynamic dispatcher thunk is unavailable for allocated slot";
            continue;
        }
        ++allocated;
        slot->install_planned = true;
        slot->status = "install planned";
        ++planned;
    }

    const int capacity = g_dispatcher_capacity.load(std::memory_order_acquire);
    g_state.dispatcher_allocated = allocated;
    g_state.dispatcher_remaining = std::max(0, capacity - required_dispatchers);
    g_state.dispatcher_blocked = count_install_blocked_slots(g_state);
    return planned;
}

const char *slot_state_name(HookSlotState state) {
    switch (state) {
        case SlotPendingResolve:
            return "PendingResolve";
        case SlotResolved:
            return "Resolved";
        case SlotHookInstalled:
            return "HookInstalled";
        case SlotInstallFailed:
            return "InstallFailed";
        case SlotDisabledByCapability:
            return "DisabledByCapability";
        case SlotFaulted:
            return "Faulted";
        case SlotSkippedKnownConflict:
            return "SkippedKnownConflict";
        default:
            return "Unknown";
    }
}

bool slot_has_mod_locked(const HookSlot &slot, const std::string &mod_id) {
    if (mod_id.empty())
        return true;

    const auto has_mod_rule = [&mod_id](const std::vector<HookSlotRuleRef> &rules) {
        for (const auto &rule : rules) {
            const auto bundle = std::find_if(
                g_state.bundles.begin(),
                g_state.bundles.end(),
                [&rule, &mod_id](const RuntimeBundle &candidate) {
                    return candidate.bundle_id == rule.bundle_id && candidate.mod_id == mod_id;
                });
            if (bundle != g_state.bundles.end())
                return true;
        }
        return false;
    };

    return has_mod_rule(slot.before_rules) ||
           has_mod_rule(slot.replace_rules) ||
           has_mod_rule(slot.after_rules);
}

size_t slot_rule_count_for_mod_locked(const HookSlot &slot, const std::string &mod_id) {
    if (mod_id.empty())
        return slot_rule_count(slot);

    const auto count_rules = [&mod_id](const std::vector<HookSlotRuleRef> &rules) {
        size_t count = 0;
        for (const auto &rule : rules) {
            const auto bundle = std::find_if(
                g_state.bundles.begin(),
                g_state.bundles.end(),
                [&rule, &mod_id](const RuntimeBundle &candidate) {
                    return candidate.bundle_id == rule.bundle_id && candidate.mod_id == mod_id;
                });
            if (bundle != g_state.bundles.end())
                ++count;
        }
        return count;
    };

    return count_rules(slot.before_rules) +
           count_rules(slot.replace_rules) +
           count_rules(slot.after_rules);
}

std::string build_slot_summary_locked(size_t max_slots, const std::string &mod_id = {}) {
    const auto matched_slots = static_cast<size_t>(std::count_if(
        g_state.slots.begin(),
        g_state.slots.end(),
        [&mod_id](const HookSlot &slot) { return slot_has_mod_locked(slot, mod_id); }));
    std::ostringstream output;
    output << "bundles=" << g_state.bundles.size()
           << " slots=" << g_state.slots.size()
           << " matchedSlots=" << matched_slots;
    if (!mod_id.empty())
        output << " filterMod=" << mod_id;
    output
           << " installed=" << count_installed_slots(g_state)
           << " boundDispatchers=" << count_bound_dispatchers_locked()
           << " dispatcherRequired=" << g_state.dispatcher_required
           << " dispatcherCapacity=" << g_dispatcher_capacity.load(std::memory_order_acquire)
           << " dispatcherNew=" << g_state.dispatcher_new
           << " dispatcherAllocated=" << g_state.dispatcher_allocated
           << " dispatcherRemaining=" << g_state.dispatcher_remaining
           << " dispatcherBlocked=" << g_state.dispatcher_blocked
           << " dispatcherReady=" << count_dispatcher_ready_slots(g_state)
           << " installable=" << count_installable_slots(g_state)
           << " blocked=" << count_install_blocked_slots(g_state)
           << " enabledRules=" << count_enabled_slot_rules(g_state)
           << " disabledRules=" << count_disabled_slot_rules(g_state);

    size_t count = 0;
    for (const auto &slot : g_state.slots) {
        if (!slot_has_mod_locked(slot, mod_id))
            continue;
        if (count++ >= max_slots) {
            output << "\n... truncated";
            break;
        }

        output << "\n#"
               << slot.slot_id
               << " " << slot.type_name << "." << slot.method_name
               << " abi=" << slot.abi_kind
               << " state=" << slot_state_name(slot.state)
               << " planned=" << (slot.install_planned ? 1 : 0)
               << " blocked=" << (slot.install_blocked ? 1 : 0)
               << " dispatch=" << slot.dispatcher_index
               << " rules=" << slot_rule_count(slot)
               << " modRules=" << slot_rule_count_for_mod_locked(slot, mod_id)
               << " enabled=" << slot_enabled_rule_count(slot)
               << " fn=" << slot.function
               << " original=" << slot.original
               << " status=" << slot.status;
    }

    return output.str();
}

struct InstallRequest {
    uint32_t slot_id = 0;
    std::string key;
    std::string type_name;
    std::string method_name;
    std::string abi_kind;
    int dispatcher_index = -1;
    void *function = nullptr;
    void *detour = nullptr;
    uint64_t before_op_mask = 0;
    uint64_t after_op_mask = 0;
};

std::vector<InstallRequest> build_install_requests_locked() {
    std::vector<InstallRequest> requests;
    prepare_install_plan_locked();

    for (auto &slot : g_state.slots) {
        if (!slot.install_planned || slot.install_blocked || slot.state != SlotResolved)
            continue;

        auto *detour = dispatcher_detour_for_slot(slot);
        if (detour == nullptr) {
            slot.install_planned = false;
            slot.install_blocked = true;
            slot.status = "no detour stub for dispatcher slot";
            continue;
        }

        const auto before_op_mask = build_before_op_mask(slot);
        const auto after_op_mask = build_after_op_mask(slot);
        const uint32_t target_kind = target_kind_for_slot(slot);
        configure_dispatcher_runtime_slot(
            slot.dispatcher_index,
            slot.slot_id,
            target_kind,
            before_op_mask,
            after_op_mask);
        publish_managed_dispatch_snapshots_locked(slot, true);
        requests.push_back(InstallRequest{
            .slot_id = slot.slot_id,
            .key = slot.key,
            .type_name = slot.type_name,
            .method_name = slot.method_name,
            .abi_kind = slot.abi_kind,
            .dispatcher_index = slot.dispatcher_index,
            .function = slot.function,
            .detour = detour,
            .before_op_mask = before_op_mask,
            .after_op_mask = after_op_mask,
        });
    }

    return requests;
}

bool process_maps_contains(const char *name) {
    std::ifstream maps("/proc/self/maps");
    if (!maps.is_open())
        return false;

    std::string line;
    while (std::getline(maps, line)) {
        if (line.find(name) != std::string::npos)
            return true;
    }
    return false;
}

bool il2cpp_runtime_has_assembly_csharp(std::string &error) {
    return ensure_il2cpp_metadata(error);
}

bool coordinator_wait_500ms(uint64_t generation) {
    std::unique_lock<std::mutex> guard(g_hook_coordinator_lock);
    g_hook_coordinator_condition.wait_for(
        guard,
        std::chrono::milliseconds(500),
        [generation] { return g_hook_work_generation != generation; });
    return g_hook_work_generation == generation;
}

bool wait_for_il2cpp_metadata_ready(uint64_t generation) {
    int il2cpp_seen_attempts = 0;
    int metadata_seen_attempts = 0;
    int runtime_probe_attempts = 0;

    for (;;) {
        {
            std::lock_guard<std::mutex> guard(g_hook_coordinator_lock);
            if (g_hook_work_generation != generation)
                return false;
        }

        if (!process_maps_contains("libil2cpp.so")) {
            if (il2cpp_seen_attempts == 0)
                LOGI("hook coordinator waiting for libil2cpp.so");
            if (!coordinator_wait_500ms(generation))
                return false;
            continue;
        }

        ++il2cpp_seen_attempts;
        if (process_maps_contains("global-metadata.dat")) {
            ++metadata_seen_attempts;
            if (metadata_seen_attempts == 1)
                LOGI("hook coordinator observed metadata; waiting for IL2CPP init settle");
            if (metadata_seen_attempts < 3) {
                if (!coordinator_wait_500ms(generation))
                    return false;
                continue;
            }
        } else if (il2cpp_seen_attempts < 10) {
            if (!coordinator_wait_500ms(generation))
                return false;
            continue;
        } else if (il2cpp_seen_attempts == 10) {
            LOGI("metadata map not observed; trying guarded runtime metadata probe");
        }

        ++runtime_probe_attempts;
        std::string probe_error;
        if (il2cpp_runtime_has_assembly_csharp(probe_error)) {
            LOGI("IL2CPP runtime metadata ready after probes=%d", runtime_probe_attempts);
            return true;
        }

        if (runtime_probe_attempts == 1 || runtime_probe_attempts % 20 == 0)
            LOGI("runtime metadata unavailable; retrying probe count=%d error=%s",
                 runtime_probe_attempts,
                 probe_error.empty() ? "<none>" : probe_error.c_str());

        if (!coordinator_wait_500ms(generation))
            return false;
    }
}

void hook_coordinator_main() {
    LOGI("native hook coordinator started");
    uint64_t observed_generation = 0;

    for (;;) {
        uint64_t generation = 0;
        {
            std::unique_lock<std::mutex> guard(g_hook_coordinator_lock);
            g_hook_coordinator_condition.wait(
                guard,
                [&] { return g_hook_work_generation != observed_generation; });
            generation = g_hook_work_generation;
            observed_generation = generation;
        }

        bool has_bundles = false;
        {
            std::lock_guard<std::mutex> guard(g_lock);
            has_bundles = !g_state.bundles.empty();
        }
        if (!has_bundles &&
            g_presentation_install_requested.load(std::memory_order_acquire) == 0)
            continue;

        if (!wait_for_il2cpp_metadata_ready(generation))
            continue;

        int metadata_attempts = 0;
        int presentation_attempts = 0;
        bool hook_pass_completed = false;
        for (;;) {
            {
                std::lock_guard<std::mutex> guard(g_hook_coordinator_lock);
                if (g_hook_work_generation != generation)
                    break;
            }

            std::string metadata_error;
            if (!ensure_il2cpp_metadata(metadata_error)) {
                ++metadata_attempts;
                if (metadata_attempts == 1 || metadata_attempts % 20 == 0) {
                    LOGI("runtime metadata unavailable; retrying count=%d error=%s",
                         metadata_attempts,
                         metadata_error.c_str());
                }
                if (!coordinator_wait_500ms(generation))
                    break;
                continue;
            }

            std::string presentation_error;
            const bool presentation_installed =
                starray::unity_presentation_sink::ensure_installed(presentation_error);
            if (presentation_installed) {
                g_presentation_install_requested.store(0, std::memory_order_release);
            } else {
                ++presentation_attempts;
                if (presentation_attempts == 1 || presentation_attempts % 20 == 0) {
                    LOGI("Unity PresentationSink unavailable; retrying count=%d error=%s",
                         presentation_attempts,
                         presentation_error.c_str());
                }
            }

            if (has_bundles && !hook_pass_completed) {
                const int resolved = modmanager_pccompat_resolve_pending_slots();
                const int planned = modmanager_pccompat_prepare_install_plan();
                const int installed = modmanager_pccompat_install_planned_slots();
                LOGI("background hook pass completed resolved=%d planned=%d installed=%d",
                     resolved,
                     planned,
                     installed);
                hook_pass_completed = true;
            }
            if (!presentation_installed &&
                g_presentation_install_requested.load(std::memory_order_acquire) != 0) {
                if (!coordinator_wait_500ms(generation))
                    break;
                continue;
            }
            break;
        }
    }
}

void start_hook_coordinator_thread_once() {
    std::call_once(g_hook_coordinator_once, [] {
        std::thread(hook_coordinator_main).detach();
    });
}

void notify_hook_coordinator() {
    modmanager_pccompat_start_hook_coordinator();
    {
        std::lock_guard<std::mutex> guard(g_hook_coordinator_lock);
        ++g_hook_work_generation;
    }
    g_hook_coordinator_condition.notify_one();
}

HookSlot *find_slot_by_key_locked(const std::string &key) {
    for (auto &slot : g_state.slots) {
        if (slot.key == key)
            return &slot;
    }

    return nullptr;
}

} // namespace

namespace starray::pccompat_metadata {

bool resolve_method(const MethodIdentity &identity,
                    ResolvedMethod &method,
                    std::string &error) {
    method = ResolvedMethod{};
    if (!ensure_il2cpp_metadata(error))
        return false;

    void *image = find_assembly_image(identity.assembly_name);
    if (image == nullptr) {
        error = "assembly not found: " + identity.assembly_name;
        return false;
    }
    void *klass = find_class(image, identity.namespace_name, identity.type_name);
    if (klass == nullptr) {
        error = "class not found: " + identity.namespace_name + "." + identity.type_name;
        return false;
    }

    RuntimeTarget target;
    target.assembly_name = identity.assembly_name;
    target.namespace_name = identity.namespace_name;
    target.type_name = identity.type_name;
    target.method_name = identity.method_name;
    target.return_type = identity.return_type;
    target.parameter_types = identity.parameter_types;
    target.has_param_count = true;
    target.param_count = static_cast<int>(identity.parameter_types.size());
    target.is_static = identity.is_static;
    target.generic_arity = 0;

    std::vector<ResolvedMethodMetadata> candidates;
    void *iterator = nullptr;
    for (;;) {
        const void *method_info = g_il2cpp_metadata.class_get_methods(klass, &iterator);
        if (method_info == nullptr)
            break;
        ResolvedMethodMetadata candidate;
        if (!read_method_metadata(method_info, candidate) ||
            candidate.name != identity.method_name ||
            !validate_method_identity(&candidate, target, error)) {
            continue;
        }
        candidates.push_back(std::move(candidate));
    }

    if (candidates.size() != 1) {
        error = "method identity resolve failed: " + identity.type_name + "." +
            identity.method_name + " matches=" + std::to_string(candidates.size());
        return false;
    }
    if (candidates[0].function == nullptr) {
        error = "method has no runtime function pointer: " + identity.type_name + "." +
            identity.method_name;
        return false;
    }

    uint32_t descriptor_slot = g_next_api_descriptor_slot.fetch_add(
        1, std::memory_order_relaxed);
    if (descriptor_slot == 0) {
        descriptor_slot = g_next_api_descriptor_slot.fetch_add(
            1, std::memory_order_relaxed);
    }
    uintptr_t protected_function = 0;
    if (!PC_COMPAT_RESOLVE_ADDRESS(
            0,
            0,
            descriptor_slot,
            0 |
                0,
            reinterpret_cast<uintptr_t>(candidates[0].function),
            &protected_function) ||
        protected_function != reinterpret_cast<uintptr_t>(candidates[0].function)) {
        error = "protected method descriptor failed: " + identity.type_name + "." +
            identity.method_name;
        return false;
    }

    method.method_info = const_cast<void *>(candidates[0].method_info);
    method.function = reinterpret_cast<void *>(protected_function);
    return true;
}

bool resolve_class(const std::string &assembly_name,
                   const std::string &namespace_name,
                   const std::string &type_name,
                   ResolvedClass &klass,
                   std::string &error) {
    klass = ResolvedClass{};
    if (!ensure_il2cpp_metadata(error))
        return false;

    void *image = find_assembly_image(assembly_name);
    if (image == nullptr &&
        (normalize_assembly_name(assembly_name) == "mscorlib" ||
         normalize_assembly_name(assembly_name) == "system.private.corelib")) {
        image = g_il2cpp_metadata.get_corlib == nullptr
            ? nullptr
            : g_il2cpp_metadata.get_corlib();
    }
    if (image == nullptr) {
        error = "assembly not found: " + assembly_name;
        return false;
    }

    void *resolved = find_class(image, namespace_name, type_name);
    if (resolved == nullptr) {
        error = "class not found: " + namespace_name + "." + type_name;
        return false;
    }
    klass.klass = resolved;
    return true;
}

bool runtime_invoke(const ResolvedMethod &method,
                    void *instance,
                    void **args,
                    void **result,
                    std::string &error) {
    if (result != nullptr)
        *result = nullptr;
    if (method.method_info == nullptr || g_il2cpp_metadata.runtime_invoke == nullptr) {
        error = "runtime invoke metadata is unavailable";
        return false;
    }

    void *exception = nullptr;
    void *value = g_il2cpp_metadata.runtime_invoke(
        method.method_info,
        instance,
        args,
        &exception);
    if (exception != nullptr) {
        error = "IL2CPP runtime invoke raised a managed exception";
        return false;
    }
    if (result != nullptr)
        *result = value;
    return true;
}

bool allocate_object(const ResolvedClass &klass,
                     void **object,
                     std::string &error) {
    if (object != nullptr)
        *object = nullptr;
    if (klass.klass == nullptr || g_il2cpp_metadata.object_new == nullptr) {
        error = "IL2CPP object allocation is unavailable";
        return false;
    }
    void *value = g_il2cpp_metadata.object_new(klass.klass);
    if (value == nullptr) {
        error = "IL2CPP object allocation returned null";
        return false;
    }
    if (object != nullptr)
        *object = value;
    return true;
}

bool allocate_reference_array(const ResolvedClass &element_class,
                              const std::vector<void *> &elements,
                              void **array,
                              std::string &error) {
    if (array != nullptr)
        *array = nullptr;
    if (element_class.klass == nullptr ||
        g_il2cpp_metadata.array_new == nullptr ||
        g_il2cpp_metadata.array_object_header_size == nullptr) {
        error = "IL2CPP reference-array allocation is unavailable";
        return false;
    }
    if (elements.size() > 64) {
        error = "IL2CPP reference-array capacity exceeded";
        return false;
    }

    void *value = g_il2cpp_metadata.array_new(element_class.klass, elements.size());
    if (value == nullptr) {
        error = "IL2CPP reference-array allocation returned null";
        return false;
    }
    const uint32_t header_size = g_il2cpp_metadata.array_object_header_size();
    if (header_size == 0 || header_size > 0x1000u) {
        error = "IL2CPP reference-array header size is invalid";
        return false;
    }
    auto *data = static_cast<char *>(value) + header_size;
    for (size_t index = 0; index < elements.size(); ++index)
        *reinterpret_cast<void **>(data + index * sizeof(void *)) = elements[index];

    if (array != nullptr)
        *array = value;
    return true;
}

bool get_type_object(const ResolvedClass &klass,
                     void **type_object,
                     std::string &error) {
    if (type_object != nullptr)
        *type_object = nullptr;
    if (klass.klass == nullptr ||
        g_il2cpp_metadata.class_get_type == nullptr ||
        g_il2cpp_metadata.type_get_object == nullptr) {
        error = "IL2CPP type-object resolution is unavailable";
        return false;
    }
    const void *type = g_il2cpp_metadata.class_get_type(klass.klass);
    void *value = type == nullptr ? nullptr : g_il2cpp_metadata.type_get_object(type);
    if (value == nullptr) {
        error = "IL2CPP type-object resolution returned null";
        return false;
    }
    if (type_object != nullptr)
        *type_object = value;
    return true;
}

bool new_managed_string(const std::string &value,
                        void **managed_string,
                        std::string &error) {
    if (managed_string != nullptr)
        *managed_string = nullptr;
    if (g_il2cpp_metadata.string_new == nullptr) {
        error = "IL2CPP string allocation is unavailable";
        return false;
    }
    void *result = g_il2cpp_metadata.string_new(value.c_str());
    if (result == nullptr) {
        error = "IL2CPP string allocation returned null";
        return false;
    }
    if (managed_string != nullptr)
        *managed_string = result;
    return true;
}

bool create_gc_handle(void *object,
                      void *&handle,
                      std::string &error) {
    handle = nullptr;
    if (object == nullptr || g_il2cpp_metadata.gchandle_new == nullptr) {
        error = "IL2CPP GCHandle allocation is unavailable";
        return false;
    }
    handle = g_il2cpp_metadata.gchandle_new(object, false);
    if (handle == nullptr) {
        error = "IL2CPP GCHandle allocation returned zero";
        return false;
    }
    return true;
}

void free_gc_handle(void *handle) {
    if (handle != nullptr && g_il2cpp_metadata.gchandle_free != nullptr)
        g_il2cpp_metadata.gchandle_free(handle);
}

bool resolve_field_offset(const ResolvedClass &klass,
                          const std::string &field_name,
                          int32_t &offset,
                          std::string &error) {
    offset = -1;
    if (klass.klass == nullptr ||
        g_il2cpp_metadata.class_get_field_from_name == nullptr ||
        g_il2cpp_metadata.field_get_offset == nullptr) {
        error = "IL2CPP field metadata is unavailable";
        return false;
    }
    void *field = g_il2cpp_metadata.class_get_field_from_name(
        klass.klass,
        field_name.c_str());
    if (field == nullptr) {
        error = "field not found: " + field_name;
        return false;
    }
    const size_t raw_offset = g_il2cpp_metadata.field_get_offset(field);
    if (raw_offset > 0x10000u) {
        error = "field offset is invalid: " + field_name;
        return false;
    }
    uint32_t descriptor_slot = g_next_scalar_descriptor_slot.fetch_add(
        1, std::memory_order_relaxed);
    if (descriptor_slot == 0) {
        descriptor_slot = g_next_scalar_descriptor_slot.fetch_add(
            1, std::memory_order_relaxed);
    }
    uint64_t protected_offset = 0;
    if (!PC_COMPAT_RESOLVE_SCALAR(
            0,
            0,
            descriptor_slot,
            static_cast<uint64_t>(raw_offset),
            &protected_offset) ||
        protected_offset != static_cast<uint64_t>(raw_offset) ||
        protected_offset > static_cast<uint64_t>(INT32_MAX)) {
        error = "protected field offset descriptor failed: " + field_name;
        return false;
    }
    offset = static_cast<int32_t>(protected_offset);
    return true;
}

}  // namespace starray::pccompat_metadata

extern "C" {

int modmanager_pccompat_prime_il2cpp_metadata() {
    std::string error;
    if (!ensure_il2cpp_metadata(error)) {
        LOGE("IL2CPP metadata prime failed: %s", error.c_str());
        return 0;
    }
    LOGI("IL2CPP metadata primed before managed compatibility startup");
    return 1;
}

void modmanager_pccompat_start_hook_coordinator() {
    starray::hud_logic::ensure_started();
    starray::async_input_bridge::ensure_registered();
    start_hook_coordinator_thread_once();
}

int modmanager_pccompat_load_hook_rules_json(const char *path) {
    if (path == nullptr || path[0] == '\0') {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = "path is empty";
        return -1;
    }

    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_operation_lock);

    {
        std::lock_guard<std::mutex> guard(g_lock);
        const auto existing = std::find_if(g_state.bundles.begin(), g_state.bundles.end(),
                                           [path](const RuntimeBundle &bundle) { return bundle.path == path; });
        if (existing != g_state.bundles.end()) {
            g_state.last_error.clear();
            LOGI("hook rules already loaded path=%s bundle=%u", path, existing->bundle_id);
            return 0;
        }
    }

    RuntimeBundle bundle;
    std::string error;
    const auto json = read_file(path);
    if (!parse_bundle(json, path, bundle, error)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = error;
        LOGE("load failed path=%s error=%s", path, error.c_str());
        return -2;
    }

    if (!pccompat_session_get_token(
            bundle.mod_id.c_str(), nullptr,
            &bundle.pc_mod_session.session_handle,
            &bundle.pc_mod_session.host_generation,
            &bundle.pc_mod_session.resource_generation)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = "PC MOD session is unavailable for JSON rule bundle";
        LOGE("load rejected path=%s mod=%s error=%s", path,
             bundle.mod_id.c_str(), g_state.last_error.c_str());
        return -3;
    }

    const auto loaded_target_count = static_cast<int>(bundle.targets.size());
    int loaded_rule_count = 0;
    for (const auto &target : bundle.targets)
        loaded_rule_count += static_cast<int>(target.rules.size());

    uint32_t bundle_id = 0;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        bundle.bundle_id = g_state.next_bundle_id++;
        bundle_id = bundle.bundle_id;
        g_state.bundles.push_back(std::move(bundle));
        rebuild_slots_locked();
        g_state.last_error.clear();
    }

    LOGI("loaded hook rules path=%s bundle=%u targets=%d rules=%d", path, bundle_id, loaded_target_count, loaded_rule_count);
    notify_hook_coordinator();
    return 0;
}

int modmanager_pccompat_load_hook_rules_bin(const char *path) {
    if (path == nullptr || path[0] == '\0') {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = "path is empty";
        return -1;
    }

    {
        std::lock_guard<std::mutex> guard(g_lock);
        const auto existing = std::find_if(
            g_state.bundles.begin(),
            g_state.bundles.end(),
            [path](const RuntimeBundle &bundle) { return bundle.path == path; });
        if (existing != g_state.bundles.end()) {
            g_state.last_error.clear();
            LOGI("binary recipe already loaded path=%s bundle=%u", path, existing->bundle_id);
            return 0;
        }
    }

    starray::pccompat_recipe::ParsedBundle parsed;
    std::string error;
    if (!starray::pccompat_recipe::parse_file(path, parsed, error)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = error;
        LOGE("binary recipe load failed path=%s error=%s", path, error.c_str());
        return -2;
    }

    RuntimeBundle bundle;
    bundle.path = path;
    if (!pccompat_session_get_token(
            parsed.mod_id.c_str(), parsed.source_assembly_sha256.data(),
            &bundle.pc_mod_session.session_handle,
            &bundle.pc_mod_session.host_generation,
            &bundle.pc_mod_session.resource_generation)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error =
            "PC MOD session is unavailable or source assembly digest changed";
        LOGE("binary recipe load rejected path=%s mod=%s error=%s", path,
             parsed.mod_id.c_str(), g_state.last_error.c_str());
        return -3;
    }
    bundle.mod_id = std::move(parsed.mod_id);
    bundle.recipe_id = std::move(parsed.recipe_id);
    bundle.compatibility = std::move(parsed.compatibility);
    bundle.required_capabilities = parsed.required_capabilities;
    bundle.ui_objects = std::move(parsed.ui_objects);
    bundle.ui_resources = std::move(parsed.ui_resources);
    bundle.ui_bytecode = std::move(parsed.bytecode);
    bundle.ui_lifecycle_programs = std::move(parsed.lifecycle_programs);
    bundle.targets.reserve(parsed.targets.size());
    for (auto &parsed_target : parsed.targets) {
        RuntimeTarget target;
        target.id = static_cast<int>(parsed_target.id);
        target.assembly_name = std::move(parsed_target.assembly_name);
        target.namespace_name = std::move(parsed_target.namespace_name);
        target.type_name = std::move(parsed_target.type_name);
        target.method_name = std::move(parsed_target.method_name);
        target.is_static = parsed_target.is_static;
        target.generic_arity = static_cast<int>(parsed_target.generic_arity);
        target.return_type = std::move(parsed_target.return_type);
        target.parameter_types = std::move(parsed_target.parameter_types);
        target.has_param_count = true;
        target.param_count = static_cast<int>(target.parameter_types.size());
        target.abi_kind = std::move(parsed_target.abi_kind);
        target.rules.reserve(parsed_target.rules.size());
        for (auto &parsed_rule : parsed_target.rules) {
            RuntimeRule rule;
            rule.id = std::move(parsed_rule.id);
            rule.feature_id = std::move(parsed_rule.feature_id);
            rule.stage_code = static_cast<int>(parsed_rule.stage_code);
            rule.op_code = static_cast<int>(parsed_rule.op_code);
            rule.required_capabilities = parsed_rule.required_capabilities;
            rule.default_enabled = parsed_rule.default_enabled;
            rule.source = std::move(parsed_rule.source);
            rule.stage = std::to_string(rule.stage_code);
            rule.op = std::to_string(rule.op_code);
            target.rules.push_back(std::move(rule));
        }
        bundle.targets.push_back(std::move(target));
    }

    const auto loaded_target_count = static_cast<int>(bundle.targets.size());
    int loaded_rule_count = 0;
    for (const auto &target : bundle.targets)
        loaded_rule_count += static_cast<int>(target.rules.size());

    // Keep lifecycle registration and bundle publication in one serialized
    // operation.  The native registry owns the immutable VM program copies;
    // g_state owns the hook target metadata.
    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_operation_lock);
    {
        std::lock_guard<std::mutex> guard(g_lock);
        const auto existing = std::find_if(
            g_state.bundles.begin(),
            g_state.bundles.end(),
            [path](const RuntimeBundle &candidate) { return candidate.path == path; });
        if (existing != g_state.bundles.end()) {
            g_state.last_error.clear();
            LOGI("binary recipe already loaded path=%s bundle=%u", path, existing->bundle_id);
            return 0;
        }
    }

    uint32_t bundle_id = 0;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        bundle.bundle_id = g_state.next_bundle_id++;
        bundle_id = bundle.bundle_id;
    }

    {
        std::lock_guard<std::mutex> managed_events_guard(g_managed_events_lock);
        auto &ring = g_managed_event_rings[bundle_id];
        if (!ring)
            ring = std::make_shared<ManagedEventRing>();
        ring->mod_id = bundle.mod_id;
        ring->registry_epoch =
            g_managed_event_registry_epoch.load(std::memory_order_relaxed);
        ring->retired.store(0, std::memory_order_release);
        ring->enabled.store(0, std::memory_order_release);
        g_managed_event_registry_generation.fetch_add(1, std::memory_order_acq_rel);
    }

    if (!starray::unity_presentation_sink::register_bundle_graph(
            bundle_id,
            bundle.mod_id,
            bundle.ui_objects,
            bundle.ui_resources,
            error)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = error;
        LOGE("binary recipe object graph registration failed path=%s bundle=%u error=%s",
             path,
             bundle_id,
             error.c_str());
        retire_managed_event_rings({bundle_id});
        return -3;
    }

    if (!starray::ui_recipe_runtime::register_bundle(
            bundle_id,
            bundle.ui_bytecode,
            bundle.ui_lifecycle_programs,
            error)) {
        starray::unity_presentation_sink::discard_bundle_graph(bundle_id);
        retire_managed_event_rings({bundle_id});
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = error;
        LOGE("binary recipe lifecycle registration failed path=%s bundle=%u error=%s",
             path,
             bundle_id,
             error.c_str());
        return -4;
    }

    {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.bundles.push_back(std::move(bundle));
        rebuild_slots_locked();
        g_state.last_error.clear();
    }

    LOGI("loaded binary recipe path=%s bundle=%u targets=%d rules=%d", path, bundle_id, loaded_target_count, loaded_rule_count);
    notify_hook_coordinator();
    return 0;
}

int modmanager_pccompat_get_loaded_bundle_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return static_cast<int>(g_state.bundles.size());
}

int modmanager_pccompat_get_loaded_target_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_targets(g_state);
}

int modmanager_pccompat_get_loaded_rule_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_rules(g_state);
}

int modmanager_pccompat_get_merged_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_merged_slots(g_state);
}

int modmanager_pccompat_resolve_pending_slots() {
    std::string error;
    if (!ensure_il2cpp_metadata(error)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = error;
        LOGE("resolve pending slots failed: %s", error.c_str());
        return -1;
    }

    int resolved_now = 0;
    int failed_now = 0;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        for (auto &bundle : g_state.bundles) {
            for (auto &target : bundle.targets) {
                if (target.resolve_attempted && target.resolved)
                    continue;

                resolve_target(target);
                if (target.resolved) {
                    ++resolved_now;
                    LOGI("resolved target bundle=%u target=%s.%s function=%p",
                         bundle.bundle_id,
                         target.type_name.c_str(),
                         target.method_name.c_str(),
                         target.function);
                } else {
                    ++failed_now;
                    LOGE("resolve target failed bundle=%u target=%s.%s error=%s",
                         bundle.bundle_id,
                         target.type_name.c_str(),
                         target.method_name.c_str(),
                         target.resolve_error.c_str());
                    if (g_state.last_error.empty())
                        g_state.last_error = target.resolve_error;
                }
            }
        }

        if (failed_now == 0)
            g_state.last_error.clear();

        rebuild_slots_locked();
    }

    LOGI("resolve pending slots completed resolved_now=%d failed_now=%d", resolved_now, failed_now);
    return failed_now == 0 ? resolved_now : -failed_now;
}

int modmanager_pccompat_get_resolved_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_resolved_slots(g_state);
}

int modmanager_pccompat_get_failed_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_failed_slots(g_state);
}

int modmanager_pccompat_get_pending_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_pending_slots(g_state);
}

int modmanager_pccompat_get_slot_rule_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_slot_rules(g_state);
}

int modmanager_pccompat_get_enabled_slot_rule_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_enabled_slot_rules(g_state);
}

int modmanager_pccompat_get_disabled_slot_rule_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_disabled_slot_rules(g_state);
}

int modmanager_pccompat_get_installable_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_installable_slots(g_state);
}

int modmanager_pccompat_get_install_blocked_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_install_blocked_slots(g_state);
}

int modmanager_pccompat_get_installed_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_installed_slots(g_state);
}

int modmanager_pccompat_get_dispatcher_ready_slot_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_dispatcher_ready_slots(g_state);
}

int modmanager_pccompat_get_dispatcher_capacity() {
    return g_dispatcher_capacity.load(std::memory_order_acquire);
}

int modmanager_pccompat_get_bound_dispatcher_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_bound_dispatchers_locked();
}

int modmanager_pccompat_get_dispatcher_required_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return g_state.dispatcher_required;
}

int modmanager_pccompat_get_dispatcher_new_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return g_state.dispatcher_new;
}

int modmanager_pccompat_get_dispatcher_allocated_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return g_state.dispatcher_allocated;
}

int modmanager_pccompat_get_dispatcher_remaining_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return g_state.dispatcher_remaining;
}

int modmanager_pccompat_get_dispatcher_blocked_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return g_state.dispatcher_blocked;
}

int modmanager_pccompat_prepare_install_plan() {
    std::lock_guard<std::mutex> guard(g_lock);
    const auto planned = prepare_install_plan_locked();
    LOGI("install plan prepared planned=%d required=%d capacity=%d bound=%d new=%d allocated=%d remaining=%d blocked=%d totalSlots=%zu",
         planned,
         g_state.dispatcher_required,
         g_dispatcher_capacity.load(std::memory_order_acquire),
         count_bound_dispatchers_locked(),
         g_state.dispatcher_new,
         g_state.dispatcher_allocated,
         g_state.dispatcher_remaining,
         g_state.dispatcher_blocked,
         g_state.slots.size());
    return planned;
}

int modmanager_pccompat_install_planned_slots() {
    std::vector<InstallRequest> requests;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        requests = build_install_requests_locked();
        if (requests.empty()) {
            LOGI("install planned slots skipped: no installable slots");
            return 0;
        }
    }

    int installed_now = 0;
    int failed_now = 0;

    for (const auto &request : requests) {
        void *origin = nullptr;
        LOGI("install slot=%u target=%s.%s abi=%s dispatcher=%d fn=%p detour=%p beforeMask=0x%llx afterMask=0x%llx",
             request.slot_id,
             request.type_name.c_str(),
             request.method_name.c_str(),
             request.abi_kind.c_str(),
             request.dispatcher_index,
             request.function,
             request.detour,
             static_cast<unsigned long long>(request.before_op_mask),
             static_cast<unsigned long long>(request.after_op_mask));

        const std::string broker_owner = "PcCompat:" + request.key;
        const uint32_t original_descriptor_slot =
            0x60000000u | (request.slot_id & 0x1fffffffu);
        const int ret = modmanager_hook_broker_install_protected(
            broker_owner.c_str(),
            0,
            0,
            original_descriptor_slot,
            request.function,
            request.detour,
            &origin);
        uintptr_t protected_origin = 0;
        const bool original_descriptor_ok = ret == 0 && origin != nullptr &&
            PC_COMPAT_RESOLVE_CONTINUATION(
                0,
                0,
                original_descriptor_slot,
                reinterpret_cast<uintptr_t>(origin),
                &protected_origin) == 1 &&
            protected_origin == reinterpret_cast<uintptr_t>(origin);
        if (ret == 0 && !original_descriptor_ok) {
            const int retired = modmanager_hook_broker_retire_owner_target(
                broker_owner.c_str(), request.function);
            LOGW("retired hook after original descriptor failure slot=%u target=%p result=%d",
                 request.slot_id,
                 request.function,
                 retired);
        }

        std::lock_guard<std::mutex> guard(g_lock);
        auto *slot = find_slot_by_key_locked(request.key);
        const bool published = original_descriptor_ok &&
            publish_original_to_dispatcher(
                request.dispatcher_index,
                reinterpret_cast<void *>(protected_origin),
                request.key,
                request.abi_kind,
                request.function);
        if (published) {
            const bool slot_matches_request = slot != nullptr &&
                slot->function == request.function &&
                slot->abi_kind == request.abi_kind &&
                (slot->dispatcher_index < 0 || slot->dispatcher_index == request.dispatcher_index) &&
                slot->state == SlotResolved;
            if (slot_matches_request) {
                slot->state = SlotHookInstalled;
                slot->original = origin;
                slot->dispatcher_index = request.dispatcher_index;
                slot->install_planned = false;
                slot->install_blocked = false;
                slot->status = "hook installed";
            } else {
                LOGE("installed hook request became stale; preserving current slot state key=%s",
                     request.key.c_str());
            }
            synchronize_bound_dispatchers_locked();
            ++installed_now;
            continue;
        }

        disable_dispatcher_runtime_slot(request.dispatcher_index);
        if (slot != nullptr) {
            slot->state = SlotInstallFailed;
            slot->original = nullptr;
            slot->install_planned = false;
            slot->install_blocked = true;
            slot->status = ret != 0
                ? "hook broker install failed ret=" + std::to_string(ret)
                : (origin == nullptr
                    ? "hook broker returned a null continuation"
                    : (!original_descriptor_ok
                        ? "protected original continuation descriptor failed"
                        : "dispatcher binding publication failed after broker install"));
        }
        if (g_state.last_error.empty())
            g_state.last_error = slot != nullptr ? slot->status : "hook broker install failed";
        LOGE("install failed slot=%u target=%s.%s ret=%d origin=%p published=%d",
             request.slot_id,
             request.type_name.c_str(),
             request.method_name.c_str(),
             ret,
             origin,
             published ? 1 : 0);
        ++failed_now;
    }

    LOGI("install planned slots completed installed_now=%d failed_now=%d installedTotal=%d",
         installed_now,
         failed_now,
         modmanager_pccompat_get_installed_slot_count());

    return failed_now == 0 ? installed_now : -failed_now;
}

const char *modmanager_pccompat_get_slot_summary() {
    static thread_local std::string summary;
    std::lock_guard<std::mutex> guard(g_lock);
    summary = build_slot_summary_locked(64);
    return summary.c_str();
}

const char *modmanager_pccompat_get_slot_summary_for_mod(const char *mod_id) {
    static thread_local std::string summary;
    std::lock_guard<std::mutex> guard(g_lock);
    summary = build_slot_summary_locked(
        64,
        mod_id != nullptr ? std::string(mod_id) : std::string{});
    return summary.c_str();
}

uint64_t modmanager_pccompat_get_approved_capabilities() {
    std::lock_guard<std::mutex> guard(g_lock);
    return g_state.approved_capabilities;
}

void modmanager_pccompat_set_approved_capabilities(uint64_t capabilities) {
    std::lock_guard<std::mutex> guard(g_lock);
    g_state.approved_capabilities = capabilities;
    rebuild_slots_locked();
}

int modmanager_pccompat_get_rule_count_for_target(const char *type_name, const char *method_name, int param_count) {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_rules_for_target_locked(type_name, method_name, param_count);
}

int modmanager_pccompat_get_rule_count_for_mod_target(const char *mod_id,
                                                       const char *type_name,
                                                       const char *method_name,
                                                       int param_count) {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_rules_for_mod_target_locked(mod_id, type_name, method_name, param_count);
}

const char *modmanager_pccompat_get_last_error() {
    // The coordinator thread may overwrite g_state.last_error as soon as the
    // lock is released; hand the caller a stable per-thread copy instead of a
    // dangling pointer into the shared string (same pattern as slot summary).
    static thread_local std::string error;
    std::lock_guard<std::mutex> guard(g_lock);
    error = g_state.last_error;
    return error.c_str();
}

void modmanager_pccompat_clear_hook_rules() {
    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_operation_lock);
    {
        std::lock_guard<std::mutex> guard(g_lock);
        const int capacity = g_dispatcher_capacity.load(std::memory_order_acquire);
        for (int index = 0; index < capacity; ++index)
            disable_dispatcher_runtime_slot(index);
        clear_owner_overlay_sessions();
        reset_overlay_runtime_state();
        reset_managed_event_state_locked();
        g_managed_prefix_order_plans.clear();
        g_managed_prefix_order_plan_staging.clear();
        g_managed_postfix_order_plans.clear();
        g_managed_postfix_order_plan_staging.clear();
        const uint32_t next_bundle_id = g_state.next_bundle_id;
        g_state = RuleState{};
        g_state.next_bundle_id = next_bundle_id;
    }
    starray::ui_recipe_runtime::clear();
    starray::unity_presentation_sink::clear_bundle_graphs();
    notify_hook_coordinator();
}

int modmanager_pccompat_unload_hook_rules_for_mod(const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    if (g_managed_prefix_callback_depth != 0) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = "cannot unload a MOD from its synchronous Prefix callback";
        return -2;
    }

    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_operation_lock);
    std::vector<uint32_t> bundle_ids;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        for (const auto &bundle : g_state.bundles) {
            if (bundle.mod_id == mod_id)
                bundle_ids.push_back(bundle.bundle_id);
        }
        retire_owner_overlay_session(mod_id);

        // Retire callback queues before publishing dispatcher snapshots without
        // this MOD. An in-flight immutable snapshot then observes retired=1 and
        // cannot enter a managed session that is being disposed.
        retire_managed_event_rings(bundle_ids);
        g_managed_prefix_order_plans.erase(mod_id);
        g_managed_prefix_order_plan_staging.erase(mod_id);
        g_managed_postfix_order_plans.erase(mod_id);
        g_managed_postfix_order_plan_staging.erase(mod_id);
        g_state.bundles.erase(
            std::remove_if(
                g_state.bundles.begin(),
                g_state.bundles.end(),
                [mod_id](const RuntimeBundle &bundle) {
                    return bundle.mod_id == mod_id;
                }),
            g_state.bundles.end());
        rebuild_slots_locked();
        g_state.last_error.clear();
    }

    for (const auto bundle_id : bundle_ids) {
        starray::ui_recipe_runtime::retire_bundle(bundle_id);
        starray::unity_presentation_sink::discard_bundle_graph(bundle_id);
    }
    notify_hook_coordinator();
    LOGI("unloaded hook rules mod=%s bundles=%zu persistentDispatchers=%d",
         mod_id,
         bundle_ids.size(),
         g_dispatcher_capacity.load(std::memory_order_acquire));
    return static_cast<int>(bundle_ids.size());
}

int modmanager_pccompat_set_recipe_presentation_enabled(
    const char *mod_id,
    int enabled) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;

    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_operation_lock);
    std::vector<uint32_t> bundle_ids;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        for (auto &bundle : g_state.bundles) {
            if (bundle.mod_id != mod_id)
                continue;
            bundle.recipe_presentation_enabled = enabled != 0;
            bundle_ids.push_back(bundle.bundle_id);
        }
    }

    for (const auto bundle_id : bundle_ids) {
        starray::ui_recipe_runtime::set_bundle_presentation_enabled(
            bundle_id,
            enabled != 0);
        starray::unity_presentation_sink::set_bundle_presentation_enabled(
            bundle_id,
            enabled != 0);
    }
    LOGI("recipe presentation mod=%s enabled=%d bundles=%zu",
         mod_id,
         enabled != 0 ? 1 : 0,
         bundle_ids.size());
    return static_cast<int>(bundle_ids.size());
}

void modmanager_pccompat_set_overlay_changed_callback(void *callback) {
    uintptr_t protected_callback = 0;
    if (callback != nullptr &&
        (PC_COMPAT_RESOLVE_CONTINUATION(
             0,
             0,
             kOverlayChangedCallbackDescriptorSlot,
             reinterpret_cast<uintptr_t>(callback),
             &protected_callback) != 1 ||
         protected_callback != reinterpret_cast<uintptr_t>(callback))) {
        LOGE("overlay callback descriptor rejected");
        callback = nullptr;
    } else if (callback != nullptr) {
        callback = reinterpret_cast<void *>(protected_callback);
    }
    g_overlay_changed_callback.store(
        reinterpret_cast<OverlayChangedCallback>(callback),
        std::memory_order_release);
}

void modmanager_pccompat_set_managed_prefix_callback(void *callback) {
    uintptr_t protected_callback = 0;
    if (callback != nullptr &&
        (PC_COMPAT_RESOLVE_CONTINUATION(
             0,
             0,
             kManagedPrefixCallbackDescriptorSlot,
             reinterpret_cast<uintptr_t>(callback),
             &protected_callback) != 1 ||
         protected_callback != reinterpret_cast<uintptr_t>(callback))) {
        LOGE("managed prefix callback descriptor rejected");
        callback = nullptr;
    } else if (callback != nullptr) {
        callback = reinterpret_cast<void *>(protected_callback);
    }
    g_managed_prefix_callback.store(
        reinterpret_cast<ManagedPrefixCallback>(callback),
        std::memory_order_release);
}

int modmanager_pccompat_get_managed_prefix_invocation_size() {
    return static_cast<int>(sizeof(PcCompatManagedPrefixInvocationV2));
}

int modmanager_pccompat_begin_managed_prefix_order_plan(const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    std::lock_guard<std::mutex> guard(g_lock);
    g_managed_prefix_order_plan_staging[mod_id].clear();
    return 1;
}

int modmanager_pccompat_add_managed_prefix_order(
    const char *mod_id,
    uint32_t prefix_id,
    int priority,
    uint64_t registration_index,
    const char *owner,
    const char *before_json,
    const char *after_json) {
    if (mod_id == nullptr || mod_id[0] == '\0' || prefix_id == 0 ||
        owner == nullptr || before_json == nullptr || after_json == nullptr) {
        return -1;
    }

    ManagedPrefixOrderMetadata metadata;
    metadata.priority = priority;
    metadata.registration_index = registration_index;
    metadata.owner = owner;
    std::string error;
    if (!extract_array_strings(before_json, metadata.before, error) ||
        !extract_array_strings(after_json, metadata.after, error)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = "managed Prefix order list is invalid: " + error;
        return -2;
    }

    std::lock_guard<std::mutex> guard(g_lock);
    const auto staging = g_managed_prefix_order_plan_staging.find(mod_id);
    if (staging == g_managed_prefix_order_plan_staging.end())
        return -3;
    staging->second[prefix_id] = std::move(metadata);
    return 1;
}

int modmanager_pccompat_commit_managed_prefix_order_plan(const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;

    int count = 0;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        const auto staging = g_managed_prefix_order_plan_staging.find(mod_id);
        if (staging == g_managed_prefix_order_plan_staging.end())
            return -2;
        count = static_cast<int>(staging->second.size());
        if (staging->second.empty())
            g_managed_prefix_order_plans.erase(mod_id);
        else
            g_managed_prefix_order_plans[mod_id] = std::move(staging->second);
        g_managed_prefix_order_plan_staging.erase(staging);
        rebuild_slots_locked();
        g_state.last_error.clear();
    }
    notify_hook_coordinator();
    return count;
}

int modmanager_pccompat_begin_managed_postfix_order_plan(const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    std::lock_guard<std::mutex> guard(g_lock);
    g_managed_postfix_order_plan_staging[mod_id].clear();
    return 1;
}

int modmanager_pccompat_add_managed_postfix_order(
    const char *mod_id,
    uint32_t postfix_id,
    int priority,
    uint64_t registration_index,
    const char *owner,
    const char *before_json,
    const char *after_json) {
    if (mod_id == nullptr || mod_id[0] == '\0' || postfix_id == 0 ||
        owner == nullptr || before_json == nullptr || after_json == nullptr) {
        return -1;
    }

    ManagedPrefixOrderMetadata metadata;
    metadata.priority = priority;
    metadata.registration_index = registration_index;
    metadata.owner = owner;
    std::string error;
    if (!extract_array_strings(before_json, metadata.before, error) ||
        !extract_array_strings(after_json, metadata.after, error)) {
        std::lock_guard<std::mutex> guard(g_lock);
        g_state.last_error = "managed Postfix order list is invalid: " + error;
        return -2;
    }

    std::lock_guard<std::mutex> guard(g_lock);
    const auto staging = g_managed_postfix_order_plan_staging.find(mod_id);
    if (staging == g_managed_postfix_order_plan_staging.end())
        return -3;
    staging->second[postfix_id] = std::move(metadata);
    return 1;
}

int modmanager_pccompat_commit_managed_postfix_order_plan(const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;

    int count = 0;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        const auto staging = g_managed_postfix_order_plan_staging.find(mod_id);
        if (staging == g_managed_postfix_order_plan_staging.end())
            return -2;
        count = static_cast<int>(staging->second.size());
        if (staging->second.empty())
            g_managed_postfix_order_plans.erase(mod_id);
        else
            g_managed_postfix_order_plans[mod_id] = std::move(staging->second);
        g_managed_postfix_order_plan_staging.erase(staging);
        rebuild_slots_locked();
        g_state.last_error.clear();
    }
    notify_hook_coordinator();
    return count;
}

int modmanager_pccompat_read_bundle_mod_id(
    uint32_t bundle_id,
    char *output,
    int capacity) {
    if (output == nullptr || capacity <= 1)
        return -1;

    std::lock_guard<std::mutex> guard(g_lock);
    const auto found = std::find_if(
        g_state.bundles.begin(),
        g_state.bundles.end(),
        [bundle_id](const RuntimeBundle &bundle) { return bundle.bundle_id == bundle_id; });
    if (found == g_state.bundles.end()) {
        output[0] = '\0';
        return -2;
    }
    if (found->mod_id.size() >= static_cast<size_t>(capacity)) {
        output[0] = '\0';
        return -3;
    }

    std::memcpy(output, found->mod_id.data(), found->mod_id.size());
    output[found->mod_id.size()] = '\0';
    return static_cast<int>(found->mod_id.size());
}

int modmanager_pccompat_set_managed_events_enabled(const char *mod_id, int enabled) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;

    std::vector<uint32_t> bundle_ids;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        for (const auto &bundle : g_state.bundles) {
            if (bundle.mod_id != mod_id)
                continue;
            bundle_ids.push_back(bundle.bundle_id);
        }
    }

    int updated = 0;
    {
        std::lock_guard<std::mutex> managed_events_guard(g_managed_events_lock);
        for (const auto bundle_id : bundle_ids) {
            const auto found = g_managed_event_rings.find(bundle_id);
            if (found == g_managed_event_rings.end())
                continue;
            auto &ring = found->second;
            std::lock_guard<std::mutex> ring_guard(ring->lock);
            if (enabled == 0) {
                ring->enabled.store(0, std::memory_order_release);
                ring->head = 0;
                ring->count = 0;
                ring->pending_lifecycle_event = {};
                ring->has_pending_lifecycle_event = false;
            } else {
                ring->enabled.store(1, std::memory_order_release);
                if (ring->has_pending_lifecycle_event) {
                    const size_t tail = (ring->head + ring->count) % ring->events.size();
                    ring->events[tail] = ring->pending_lifecycle_event;
                    ++ring->count;
                    ++ring->pushed;
                    ring->pending_lifecycle_event = {};
                    ring->has_pending_lifecycle_event = false;
                }
            }
            ++updated;
        }
        if (updated != 0)
            g_managed_event_registry_generation.fetch_add(1, std::memory_order_acq_rel);
    }
    LOGI("managed events mod=%s enabled=%d rings=%d", mod_id, enabled != 0 ? 1 : 0, updated);
    return updated;
}

// Registers a MOD-owned host component instance so the render-callback hook dispatches for it. The
// pointer is an IL2CPP object address, valid only while the managed bridge holds a lease on it; the
// bridge unregisters before destroying it, so a stale pointer never reaches the comparison.
//
// Returns the resulting registration count for the MOD, or negative on argument failure. Registering
// the same pointer twice is not an error - the flat set is deduplicated - because a MOD's pool may
// re-register a recycled object.
int modmanager_pccompat_register_managed_render_host(const char *mod_id, uint64_t instance_ptr) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    if (instance_ptr == 0)
        return -2;

    std::lock_guard<std::mutex> guard(g_managed_render_hosts_lock);
    auto &hosts = g_managed_render_hosts_by_mod[mod_id];
    if (std::find(hosts.begin(), hosts.end(), instance_ptr) == hosts.end())
        hosts.push_back(instance_ptr);
    republish_managed_render_hosts_locked();
    return static_cast<int>(hosts.size());
}

int modmanager_pccompat_unregister_managed_render_host(const char *mod_id, uint64_t instance_ptr) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    if (instance_ptr == 0)
        return -2;

    std::lock_guard<std::mutex> guard(g_managed_render_hosts_lock);
    const auto found = g_managed_render_hosts_by_mod.find(mod_id);
    if (found == g_managed_render_hosts_by_mod.end())
        return 0;
    auto &hosts = found->second;
    const auto removed = std::remove(hosts.begin(), hosts.end(), instance_ptr);
    const int count = static_cast<int>(std::distance(removed, hosts.end()));
    hosts.erase(removed, hosts.end());
    if (hosts.empty())
        g_managed_render_hosts_by_mod.erase(found);
    republish_managed_render_hosts_locked();
    return count;
}

// Session teardown. Dropping every entry for one MOD must not disturb another's, which is why the
// per-MOD lists are kept alongside the flat set rather than derived from it.
int modmanager_pccompat_clear_managed_render_hosts(const char *mod_id) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;

    std::lock_guard<std::mutex> guard(g_managed_render_hosts_lock);
    const auto found = g_managed_render_hosts_by_mod.find(mod_id);
    if (found == g_managed_render_hosts_by_mod.end())
        return 0;
    const int count = static_cast<int>(found->second.size());
    g_managed_render_hosts_by_mod.erase(found);
    republish_managed_render_hosts_locked();
    LOGI("managed render hosts cleared mod=%s count=%d", mod_id, count);
    return count;
}

int modmanager_pccompat_managed_render_host_count() {
    const auto hosts = std::atomic_load_explicit(
        &g_managed_render_hosts,
        std::memory_order_acquire);
    return hosts ? static_cast<int>(hosts->size()) : 0;
}

int modmanager_pccompat_drain_managed_events(const char *mod_id,
                                             void *output,
                                             int capacity_events,
                                             uint64_t *dropped_out) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    if (capacity_events < 0 || (capacity_events > 0 && output == nullptr))
        return -2;

    int drained = 0;
    uint64_t dropped_total = 0;
    const auto &rings = managed_event_rings_for_mod(mod_id);
    static thread_local std::vector<std::unique_lock<std::mutex>> ring_locks;
    ring_locks.clear();
    ring_locks.reserve(rings.size());
    for (const auto &ring_ref : rings) {
        auto *ring = ring_ref.get();
        if (ring == nullptr)
            continue;
        ring_locks.emplace_back(ring->lock);
        dropped_total += ring->dropped;
    }

    // A MOD may own more than one recipe bundle/ring after companion-assembly
    // expansion. Merge their FIFO heads by the global hook-time sequence so the
    // managed cross-MOD merge receives one monotonic stream per MOD.
    while (drained < capacity_events) {
        ManagedEventRing *selected = nullptr;
        uint64_t selected_sequence = std::numeric_limits<uint64_t>::max();
        for (const auto &ring_ref : rings) {
            auto *ring = ring_ref.get();
            if (ring == nullptr || ring->count == 0)
                continue;
            const auto sequence =
                ring->events[ring->head].event.dispatch_sequence;
            if (selected == nullptr || sequence < selected_sequence) {
                selected = ring;
                selected_sequence = sequence;
            }
        }
        if (selected == nullptr)
            break;

        const auto &event = selected->events[selected->head].event;
        std::memcpy(
            static_cast<char *>(output) +
                static_cast<size_t>(drained) * sizeof(PcCompatManagedEventV2),
            &event,
            sizeof(PcCompatManagedEventV2));
        selected->head = (selected->head + 1) % selected->events.size();
        --selected->count;
        ++drained;
    }

    if (dropped_out != nullptr)
        *dropped_out = dropped_total;
    ring_locks.clear();
    return drained;
}

void modmanager_pccompat_request_presentation_sink_install() {
    start_hook_coordinator_thread_once();
    if (g_presentation_install_requested.exchange(1, std::memory_order_acq_rel) != 0)
        return;
    {
        std::lock_guard<std::mutex> guard(g_hook_coordinator_lock);
        ++g_hook_work_generation;
    }
    g_hook_coordinator_condition.notify_one();
}

int modmanager_pccompat_get_managed_event_record_size() {
    return static_cast<int>(sizeof(PcCompatManagedEventV2));
}

#pragma pack(push, 1)
struct PcCompatManagedEventStatsV1 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t rings;
    uint32_t enabled_rings;
    uint64_t pushed_total;
    uint64_t queued_current;
    uint64_t dropped_total;
};
#pragma pack(pop)
static_assert(sizeof(PcCompatManagedEventStatsV1) == 40);
constexpr uint32_t kManagedEventStatsAbiVersion = 1;

int modmanager_pccompat_read_managed_event_stats(const char *mod_id,
                                                 void *output,
                                                 uint32_t output_size) {
    if (mod_id == nullptr || mod_id[0] == '\0')
        return -1;
    if (output == nullptr || output_size < sizeof(PcCompatManagedEventStatsV1))
        return -2;

    PcCompatManagedEventStatsV1 stats{};
    stats.struct_size = sizeof(PcCompatManagedEventStatsV1);
    stats.abi_version = kManagedEventStatsAbiVersion;
    for (const auto &ring_ref : managed_event_rings_for_mod(mod_id)) {
        auto *ring = ring_ref.get();
        if (ring == nullptr)
            continue;
        std::lock_guard<std::mutex> ring_guard(ring->lock);
        ++stats.rings;
        stats.enabled_rings +=
            ring->enabled.load(std::memory_order_acquire) != 0 ? 1u : 0u;
        stats.pushed_total += ring->pushed;
        stats.queued_current += ring->count +
            (ring->has_pending_lifecycle_event ? 1u : 0u);
        stats.dropped_total += ring->dropped;
    }

    std::memcpy(output, &stats, sizeof(stats));
    return 0;
}

// Reads a boxed IL2CPP value-type object for the managed callback dispatcher:
// resolves the concrete type name (e.g. a game enum like "States") and reads the
// unboxed payload as a sign-extended int32 (all catalog value types fit in 32 bits).
// Must run on a Unity-attached thread; the managed dispatcher calls it on UnityMain.
int modmanager_pccompat_read_boxed_value_info(void *boxed,
                                              char *name_out,
                                              int name_capacity,
                                              int64_t *value_out) {
    if (boxed == nullptr || name_out == nullptr || name_capacity <= 0 || value_out == nullptr)
        return -1;

    std::string error;
    if (!ensure_il2cpp_metadata(error))
        return -2;
    if (g_il2cpp_metadata.object_get_class == nullptr ||
        g_il2cpp_metadata.class_get_type == nullptr ||
        g_il2cpp_metadata.type_get_name == nullptr ||
        g_il2cpp_metadata.object_unbox == nullptr)
        return -3;

    void *klass = g_il2cpp_metadata.object_get_class(boxed);
    if (klass == nullptr)
        return -4;
    const void *type = g_il2cpp_metadata.class_get_type(klass);
    if (type == nullptr)
        return -5;
    char *raw_name = g_il2cpp_metadata.type_get_name(type);
    if (raw_name == nullptr)
        return -6;

    const size_t length = std::strlen(raw_name);
    if (length >= static_cast<size_t>(name_capacity)) {
        if (g_il2cpp_metadata.free_memory != nullptr)
            g_il2cpp_metadata.free_memory(raw_name);
        return -7;
    }
    std::memcpy(name_out, raw_name, length + 1);
    if (g_il2cpp_metadata.free_memory != nullptr)
        g_il2cpp_metadata.free_memory(raw_name);

    void *payload = g_il2cpp_metadata.object_unbox(boxed);
    if (payload == nullptr)
        return -8;
    *value_out = static_cast<int64_t>(*static_cast<int32_t *>(payload));
    return 0;
}

// Reads a patch target's exact signature out of live IL2CPP metadata.
//
// validate_method_identity above refuses any target whose declared return type or parameter types
// do not match runtime metadata exactly - that strictness is what keeps hook installation
// fail-closed. The importer only reads the MOD assembly, so for a target outside the hand-audited
// fixed-op catalog it has no way to produce a signature the resolver would accept. This export is
// the answer: import runs inside the game process with IL2CPP already loaded, so it can ask.
//
// Everything here is metadata reads (class_from_name, class_get_methods, method_get_*). No object
// is allocated, no managed code runs, and nothing is invoked, so it is safe off UnityMain - the
// importer runs on a worker thread.
//
// Output is a UTF-8 record of '\n'-separated fields, so the managed side needs no struct ABI:
//   assembly \n namespace \n type \n method \n "static"|"instance" \n returnType \n param...
// A method with no parameters simply ends after returnType. Return codes:
//    0  resolved, record written
//   -1  bad arguments
//   -2  IL2CPP metadata unavailable (error text written to |output| when it fits)
//   -3  type not found in any loaded image
//   -4  no method with that name (or none survived the declared parameter count)
//   -5  ambiguous: more than one candidate survived
//   -6  output buffer too small
//
// declared_param_count is the arity the patch attribute wrote, or -1 when it wrote none. It only
// narrows candidates; it never invents one.
int modmanager_pccompat_resolve_target_signature(const char *assembly_name,
                                                 const char *namespace_name,
                                                 const char *type_name,
                                                 const char *method_name,
                                                 int declared_param_count,
                                                 char *output,
                                                 int output_capacity) {
    if (type_name == nullptr || type_name[0] == '\0' ||
        method_name == nullptr || method_name[0] == '\0' ||
        output == nullptr || output_capacity <= 0) {
        return -1;
    }
    output[0] = '\0';

    const auto write_text = [output, output_capacity](const std::string &text) {
        if (text.size() >= static_cast<size_t>(output_capacity))
            return false;
        std::memcpy(output, text.c_str(), text.size() + 1);
        return true;
    };

    std::string error;
    if (!ensure_il2cpp_metadata(error)) {
        write_text(error);
        return -2;
    }
    if (g_il2cpp_metadata.class_from_name == nullptr ||
        g_il2cpp_metadata.class_get_methods == nullptr) {
        write_text("il2cpp metadata does not expose class/method enumeration");
        return -2;
    }

    const std::string requested_assembly = assembly_name == nullptr ? std::string{} : assembly_name;
    const std::string requested_namespace = namespace_name == nullptr ? std::string{} : namespace_name;
    const std::string requested_type = type_name;

    // g_il2cpp_metadata.images is keyed by the lowercased, ".dll"-stripped name, so report the
    // image's own display name instead - both round-trip through normalize_assembly_name, but only
    // one of them reads correctly in the audit report.
    const auto image_display_name = [](void *image, const std::string &fallback) {
        const char *raw = image == nullptr || g_il2cpp_metadata.image_get_name == nullptr
            ? nullptr
            : g_il2cpp_metadata.image_get_name(image);
        if (raw == nullptr || *raw == '\0')
            return fallback;
        std::string name = raw;
        if (name.ends_with(".dll"))
            name.resize(name.size() - 4);
        return name;
    };

    // A named assembly is honoured exactly; otherwise every loaded image is searched. Two images
    // holding the same type is itself an ambiguity, not a reason to pick the first one.
    std::string resolved_assembly;
    void *klass = nullptr;
    if (!requested_assembly.empty()) {
        void *image = find_assembly_image(requested_assembly);
        if (image == nullptr) {
            write_text("assembly not found: " + requested_assembly);
            return -3;
        }
        klass = find_class(image, requested_namespace, requested_type);
        resolved_assembly = image_display_name(image, requested_assembly);
    } else {
        int matches = 0;
        for (const auto &entry : g_il2cpp_metadata.images) {
            void *candidate = find_class(entry.second, requested_namespace, requested_type);
            if (candidate == nullptr)
                continue;
            ++matches;
            klass = candidate;
            resolved_assembly = image_display_name(entry.second, entry.first);
        }
        if (matches > 1) {
            write_text("type is present in " + std::to_string(matches) +
                       " loaded images: " + requested_type);
            return -5;
        }
    }

    if (klass == nullptr) {
        write_text("class not found: " +
                   (requested_namespace.empty() ? requested_type
                                                : requested_namespace + "." + requested_type));
        return -3;
    }

    std::vector<ResolvedMethodMetadata> candidates;
    void *iterator = nullptr;
    for (;;) {
        const void *method_info = g_il2cpp_metadata.class_get_methods(klass, &iterator);
        if (method_info == nullptr)
            break;
        ResolvedMethodMetadata candidate;
        if (!read_method_metadata(method_info, candidate) || candidate.name != method_name)
            continue;
        // The strict resolver rejects generics outright, so a candidate the importer could never
        // use is filtered here rather than reported as an ambiguity.
        if (candidate.is_generic)
            continue;
        if (declared_param_count >= 0 &&
            candidate.params.size() != static_cast<size_t>(declared_param_count)) {
            continue;
        }
        candidates.push_back(std::move(candidate));
    }

    if (candidates.empty()) {
        write_text("no non-generic method named " + std::string(method_name) + " on " +
                   requested_type +
                   (declared_param_count >= 0
                        ? " with " + std::to_string(declared_param_count) + " parameters"
                        : std::string{}));
        return -4;
    }
    if (candidates.size() != 1) {
        write_text(std::to_string(candidates.size()) + " overloads of " + requested_type + "." +
                   method_name +
                   " match; the patch attribute must declare its argument types");
        return -5;
    }

    const auto &resolved = candidates.front();
    std::string record;
    record.reserve(256);
    record.append(resolved_assembly).push_back('\n');
    record.append(requested_namespace).push_back('\n');
    record.append(requested_type).push_back('\n');
    record.append(resolved.name).push_back('\n');
    record.append(resolved.is_static ? "static" : "instance").push_back('\n');
    record.append(resolved.return_type);
    for (const auto &parameter : resolved.params) {
        record.push_back('\n');
        record.append(parameter);
    }

    if (!write_text(record))
        return -6;
    return 0;
}

void modmanager_pccompat_set_resource_changer_settings(const char *mod_id,
                                                       int64_t session_generation,
                                                       int change_rabbit,
                                                       int change_ball_color,
                                                       int change_tile_color,
                                                       float planet_r,
                                                       float planet_g,
                                                       float planet_b,
                                                       float planet_a,
                                                       float title_r,
                                                       float title_g,
                                                       float title_b,
                                                       float title_a,
                                                       float tile_r,
                                                       float tile_g,
                                                       float tile_b,
                                                       float tile_a,
                                                       const char *resource_pack_name) {
    if (mod_id == nullptr || *mod_id == '\0') {
        LOGE("ResourceChanger state rejected: empty mod id");
        return;
    }
    if (!pc_mod_resource_session_active(mod_id, session_generation)) {
        LOGW("ResourceChanger state rejected: inactive PC MOD session mod=%s generation=%lld",
             mod_id, static_cast<long long>(session_generation));
        return;
    }

    const auto finite_or = [](float value, float fallback) {
        return std::isfinite(value) ? value : fallback;
    };

    uint32_t feature_mask = 0;
    if (change_rabbit != 0)
        feature_mask |= kResourceContributionRabbit;
    if (change_ball_color != 0)
        feature_mask |= kResourceContributionPlanet;
    if (change_tile_color != 0)
        feature_mask |= kResourceContributionTile;

    const ResourceOwnerKey owner{mod_id, session_generation};
    bool stale = false;
    uint32_t transition_mask = 0;
    std::string active_mod_id;
    int64_t active_generation = 0;
    {
        std::lock_guard<std::mutex> guard(g_resource_state_lock);
        auto latest = g_resource_latest_generation_by_mod.find(owner.mod_id);
        if (latest != g_resource_latest_generation_by_mod.end() &&
            session_generation < latest->second) {
            stale = true;
        } else {
            if (latest == g_resource_latest_generation_by_mod.end() ||
                session_generation > latest->second) {
                for (auto it = g_resource_contributions.begin();
                     it != g_resource_contributions.end();) {
                    if (it->first.mod_id == owner.mod_id) {
                        it = g_resource_contributions.erase(it);
                    } else {
                        ++it;
                    }
                }
                g_resource_latest_generation_by_mod[owner.mod_id] =
                    session_generation;
            }

            if (feature_mask == 0) {
                g_resource_contributions.erase(owner);
            } else {
                auto [entry, inserted] = g_resource_contributions.try_emplace(owner);
                ResourceContribution &contribution = entry->second;
                if (inserted) {
                    contribution.registration_sequence =
                        ++g_resource_contribution_sequence;
                }
                contribution.feature_mask = feature_mask;
                contribution.planet_color = ColorValue{
                    finite_or(planet_r, kResourceDefaultPlanetColor.r),
                    finite_or(planet_g, kResourceDefaultPlanetColor.g),
                    finite_or(planet_b, kResourceDefaultPlanetColor.b),
                    finite_or(planet_a, kResourceDefaultPlanetColor.a),
                };
                contribution.title_color = ColorValue{
                    finite_or(title_r, kResourceDefaultTitleColor.r),
                    finite_or(title_g, kResourceDefaultTitleColor.g),
                    finite_or(title_b, kResourceDefaultTitleColor.b),
                    finite_or(title_a, kResourceDefaultTitleColor.a),
                };
                contribution.tile_color = ColorValue{
                    finite_or(tile_r, kResourceDefaultTileColor.r),
                    finite_or(tile_g, kResourceDefaultTileColor.g),
                    finite_or(tile_b, kResourceDefaultTileColor.b),
                    finite_or(tile_a, kResourceDefaultTileColor.a),
                };
                contribution.resource_pack_name =
                    resource_pack_name != nullptr ? resource_pack_name : "";
            }
            transition_mask = publish_resource_effective_state_locked();
            active_mod_id = g_resource_state_mod_id;
            active_generation = g_resource_state_generation;
        }
    }

    if (stale) {
        LOGI("ResourceChanger stale state ignored mod=%s generation=%lld",
             mod_id,
             static_cast<long long>(session_generation));
        return;
    }

    refresh_resource_rabbit_sprite_projection();
    LOGI("ResourceChanger contribution mod=%s generation=%lld rabbit=%d ball=%d tile=%d active=%s/%lld transition=0x%x",
         mod_id,
         static_cast<long long>(session_generation),
         change_rabbit != 0 ? 1 : 0,
         change_ball_color != 0 ? 1 : 0,
         change_tile_color != 0 ? 1 : 0,
         active_mod_id.empty() ? "<none>" : active_mod_id.c_str(),
         static_cast<long long>(active_generation),
         transition_mask);
}

int modmanager_pccompat_publish_resource_changer_sprite(
        const char *mod_id,
        int64_t session_generation,
        void *sprite) {
    if (mod_id == nullptr || *mod_id == '\0' || session_generation <= 0 || sprite == nullptr)
        return -1;
    if (!pc_mod_resource_session_active(mod_id, session_generation))
        return -4;
    std::string error;
    if (!ensure_il2cpp_metadata(error) || g_il2cpp_metadata.gchandle_new == nullptr)
        return -2;

    void *const next_handle = g_il2cpp_metadata.gchandle_new(sprite, false);
    if (next_handle == nullptr)
        return -3;

    const ResourceOwnerKey owner{mod_id, session_generation};
    std::vector<void *> stale_handles;
    bool stale_generation = false;
    {
        std::lock_guard<std::mutex> guard(g_resource_asset_lock);
        auto latest = g_resource_sprite_latest_generation_by_mod.find(owner.mod_id);
        if (latest != g_resource_sprite_latest_generation_by_mod.end() &&
            session_generation < latest->second) {
            stale_generation = true;
        } else {
            if (latest == g_resource_sprite_latest_generation_by_mod.end() ||
                session_generation > latest->second) {
                for (auto it = g_resource_sprite_contributions.begin();
                     it != g_resource_sprite_contributions.end();) {
                    if (it->first.mod_id == owner.mod_id) {
                        stale_handles.push_back(it->second.handle);
                        it = g_resource_sprite_contributions.erase(it);
                    } else {
                        ++it;
                    }
                }
                g_resource_sprite_latest_generation_by_mod[owner.mod_id] =
                    session_generation;
            }

            auto [entry, inserted] =
                g_resource_sprite_contributions.try_emplace(owner);
            if (inserted) {
                entry->second.registration_sequence = ++g_resource_sprite_sequence;
            } else if (entry->second.handle != nullptr) {
                stale_handles.push_back(entry->second.handle);
            }
            entry->second.handle = next_handle;
            refresh_resource_rabbit_sprite_projection_locked();
        }
    }
    if (stale_generation) {
        free_resource_handle(next_handle);
        return 0;
    }
    for (void *handle : stale_handles)
        free_resource_handle(handle);
    LOGI("ResourceChanger VirtualBundle sprite published mod=%s generation=%lld",
         mod_id,
         static_cast<long long>(session_generation));
    return 1;
}

int modmanager_pccompat_retire_resource_changer_sprite(
        const char *mod_id,
        int64_t session_generation) {
    if (mod_id == nullptr || *mod_id == '\0' || session_generation <= 0)
        return -1;
    void *stale_handle = nullptr;
    {
        std::lock_guard<std::mutex> guard(g_resource_asset_lock);
        const ResourceOwnerKey owner{mod_id, session_generation};
        const auto contribution = g_resource_sprite_contributions.find(owner);
        if (contribution == g_resource_sprite_contributions.end())
            return 0;
        stale_handle = contribution->second.handle;
        g_resource_sprite_contributions.erase(contribution);
        refresh_resource_rabbit_sprite_projection_locked();
    }
    free_resource_handle(stale_handle);
    return 1;
}

int modmanager_pccompat_apply_pending_resource_changer_state() {
    return apply_pending_resource_changer_state();
}

int modmanager_pccompat_get_loaded_ui_lifecycle_program_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_ui_lifecycle_programs(g_state);
}

int modmanager_pccompat_get_loaded_ui_object_node_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_ui_object_nodes(g_state);
}

int modmanager_pccompat_get_loaded_ui_component_op_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_ui_component_ops(g_state);
}

int modmanager_pccompat_get_loaded_ui_resource_binding_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_ui_resource_bindings(g_state);
}

int modmanager_pccompat_get_loaded_ui_bytecode_instruction_count() {
    std::lock_guard<std::mutex> guard(g_lock);
    return count_ui_bytecode_instructions(g_state);
}

void modmanager_pccompat_set_application_focus_state(int resumed,
                                                     int window_focused) {
    uint32_t state = kApplicationFocusKnown;
    if (resumed != 0)
        state |= kApplicationResumed;
    if (window_focused != 0)
        state |= kApplicationWindowFocused;
    g_application_focus_state.store(state, std::memory_order_release);
}

int modmanager_pccompat_application_is_focused() {
    const uint32_t state = g_application_focus_state.load(std::memory_order_acquire);
    if ((state & kApplicationFocusKnown) == 0)
        return 1;
    return (state & (kApplicationResumed | kApplicationWindowFocused)) ==
                   (kApplicationResumed | kApplicationWindowFocused)
               ? 1
               : 0;
}

void modmanager_pccompat_observe_touch_input(int action,
                                             int pointer_id,
                                             int pointer_count,
                                             int64_t event_time_ms,
                                             float x,
                                             float y,
                                             int viewport_width,
                                             int viewport_height,
                                             int source,
                                             int device_id,
                                             int android_flags) {
    if (!pccompat_runtime_enabled(0))
        return;
    starray::hud_logic::ensure_started();
    starray::realtime::observe_touch_raw(
        starray::realtime::InputProducer::OfficialActivity,
        action,
        pointer_id,
        pointer_count,
        event_time_ms > 0 ? event_time_ms * 1'000'000LL : 0,
        x,
        y,
        viewport_width,
        viewport_height,
        source,
        device_id,
        android_flags);
}

void modmanager_pccompat_observe_key_input(int action,
                                           int key_code,
                                           int scan_code,
                                           int meta_state,
                                           int device_id,
                                           int repeat_count,
                                           int64_t event_time_ms,
                                           int source,
                                           int android_flags) {
    if (!pccompat_runtime_enabled(0))
        return;
    starray::hud_logic::ensure_started();
    starray::realtime::observe_key_raw(
        starray::realtime::InputProducer::OfficialActivity,
        action,
        key_code,
        scan_code,
        meta_state,
        device_id,
        repeat_count,
        event_time_ms > 0 ? event_time_ms * 1'000'000LL : 0,
        source,
        android_flags);
}

void modmanager_pccompat_set_external_input_devices(uint32_t flags) {
    if (!pccompat_runtime_enabled(0))
        return;
    starray::realtime::set_external_input_devices(flags);
}

int modmanager_pccompat_read_external_input_devices(void *output,
                                                    uint32_t output_size) {
    using starray::realtime::ExternalInputDeviceSnapshot;
    if (output == nullptr || output_size < sizeof(ExternalInputDeviceSnapshot))
        return -1;
    const auto *request = static_cast<const ExternalInputDeviceSnapshot *>(output);
    if (request->abi_version != 1 ||
        request->struct_size != sizeof(ExternalInputDeviceSnapshot)) {
        return -1;
    }
    const auto snapshot = starray::realtime::read_external_input_devices();
    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

int modmanager_pccompat_read_legacy_input_snapshot(void *output,
                                                   uint32_t output_size) {
    using starray::realtime::LegacyInputSnapshot;
    if (output == nullptr || output_size < sizeof(LegacyInputSnapshot))
        return -1;

    const auto *request = static_cast<const LegacyInputSnapshot *>(output);
    if (request->struct_size != sizeof(LegacyInputSnapshot) ||
        request->abi_version != 1) {
        return -1;
    }

    LegacyInputSnapshot snapshot{};
    if (!starray::realtime::read_legacy_input_snapshot(
            request->generation,
            snapshot)) {
        return 0;
    }
    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

int modmanager_pccompat_get_active_input_producer() {
    switch (starray::realtime::active_producer()) {
        case starray::realtime::InputProducer::AsyncInput:
            return 2;
        case starray::realtime::InputProducer::OfficialActivity:
            return 1;
        default:
            return 0;
    }
}

int modmanager_pccompat_set_touch_lane_mapping_mode(int mode) {
    using starray::realtime::TouchLaneMappingMode;
    if (mode != static_cast<int>(TouchLaneMappingMode::ScreenRegions) &&
        mode != static_cast<int>(TouchLaneMappingMode::TouchContacts)) {
        return 0;
    }
    return starray::realtime::set_touch_lane_mapping_mode(
        static_cast<TouchLaneMappingMode>(mode)) ? 1 : 0;
}

int modmanager_pccompat_set_touch_contact_reuse_delay_ms(int milliseconds) {
    return starray::realtime::set_touch_contact_reuse_delay_ms(milliseconds)
        ? 1
        : 0;
}

int modmanager_pccompat_read_raw_input_events(void *header_output,
                                              uint32_t header_size,
                                              void *events_output,
                                              uint32_t event_size,
                                              uint32_t event_capacity) {
    if (header_output == nullptr ||
        header_size < sizeof(PcCompatRawInputReadV1) ||
        event_size != sizeof(PcCompatRawInputEventV1) ||
        event_capacity > 256 ||
        (event_capacity != 0 && events_output == nullptr)) {
        return -1;
    }

    const auto *request = static_cast<const PcCompatRawInputReadV1 *>(header_output);
    if (request->struct_size != sizeof(PcCompatRawInputReadV1) ||
        request->abi_version != kRawInputReadAbiVersion ||
        request->capacity != event_capacity) {
        return -1;
    }

    std::array<starray::realtime::InputEvent, 256> source_events{};
    const auto read = starray::realtime::read_events(
        request->cursor,
        source_events.data(),
        event_capacity);
    auto *destination = static_cast<PcCompatRawInputEventV1 *>(events_output);
    for (size_t index = 0; index < read.count; ++index) {
        const auto &event = source_events[index];
        destination[index] = PcCompatRawInputEventV1{
            .sequence = event.sequence,
            .raw_ns = event.raw_ns,
            .state_generation = event.state_generation,
            .session_generation = event.session_generation,
            .producer_epoch = event.producer_epoch,
            .producer = static_cast<uint8_t>(event.producer),
            .source = static_cast<uint8_t>(event.source),
            .phase = static_cast<uint8_t>(event.phase),
            .reserved0 = 0,
            .code = event.code,
            .slot = event.slot,
            .pointer_count = event.pointer_count,
            .scan_code = event.scan_code,
            .meta_state = event.meta_state,
            .device_id = event.device_id,
            .repeat_count = event.repeat_count,
            .android_flags = event.android_flags,
            .source_code = event.source_code,
            .viewport_width = event.viewport_width,
            .viewport_height = event.viewport_height,
            .x = event.x,
            .y = event.y,
            .flags = event.flags,
        };
    }

    const PcCompatRawInputReadV1 response{
        .struct_size = sizeof(PcCompatRawInputReadV1),
        .abi_version = kRawInputReadAbiVersion,
        .cursor = read.cursor,
        .dropped_before_cursor = read.dropped_before_cursor,
        .count = static_cast<uint32_t>(read.count),
        .capacity = event_capacity,
    };
    std::memcpy(header_output, &response, sizeof(response));
    return 1;
}

int modmanager_pccompat_wait_raw_input_change(uint64_t cursor,
                                              int32_t timeout_ms) {
    const auto before = starray::realtime::read_input_snapshot();
    if (before.latest_sequence > cursor)
        return 1;
    const int32_t bounded_timeout = std::clamp(timeout_ms, 1, 1000);
    const int64_t deadline_ns = starray::realtime::monotonic_now_ns() +
        static_cast<int64_t>(bounded_timeout) * 1'000'000LL;
    starray::realtime::wait_for_change(
        cursor,
        before.generation,
        deadline_ns);
    return starray::realtime::read_input_snapshot().latest_sequence > cursor ? 1 : 0;
}

void modmanager_pccompat_interrupt_raw_input_wait() {
    starray::realtime::notify_waiters();
}

int modmanager_pccompat_read_input_hud_snapshot(void *output, uint32_t output_size) {
    if (output == nullptr || output_size < sizeof(PcCompatInputHudSnapshotV1))
        return -1;

    const auto *request = static_cast<const PcCompatInputHudSnapshotV1 *>(output);
    if (request->struct_size != sizeof(PcCompatInputHudSnapshotV1) ||
        request->abi_version != kInputHudSnapshotAbiVersion) {
        return -1;
    }

    starray::hud_logic::ensure_started();
    const auto producer = starray::realtime::read_input_snapshot();
    starray::hud_logic::CompletedInputSnapshot completed{};
    const bool completed_current =
        starray::hud_logic::read_latest_input_snapshot(completed) &&
        completed.session_generation == producer.session_generation;
    const uint32_t current_publication_generation = completed_current
        ? completed.publication_generation
        : 0;
    if (request->publication_generation == current_publication_generation)
        return 0;

    starray::hud_logic::TouchLaneProjectionSnapshot projection{};
    if (completed_current) {
        if (!starray::hud_logic::select_touch_lane_projection(
                completed,
                request->touch_lane_count,
                projection)) {
            return -2;
        }
    } else if (request->touch_lane_count == 2 ||
               request->touch_lane_count == 4 ||
               request->touch_lane_count == 6 ||
               request->touch_lane_count == 8 ||
               request->touch_lane_count == 10) {
        projection.lane_count = request->touch_lane_count;
    } else {
        return -2;
    }

    PcCompatInputHudSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    snapshot.abi_version = kInputHudSnapshotAbiVersion;
    snapshot.publication_generation = current_publication_generation;
    snapshot.session_generation = producer.session_generation;
    snapshot.source_generation = completed_current
        ? completed.source_generation
        : producer.generation;
    snapshot.touch_lane_count = projection.lane_count;
    snapshot.touch_lane_held_mask = projection.held_mask;
    snapshot.touch_lane_last_down_mask = projection.last_down_mask;
    snapshot.touch_lane_last_up_mask = projection.last_up_mask;
    snapshot.input_total_count = completed_current
        ? completed.total_count
        : producer.total_count;
    snapshot.keyboard_held_count = completed_current
        ? completed.keyboard_held_count
        : producer.keyboard_held_count;
    snapshot.input_kps = completed_current ? completed.kps : producer.kps;
    snapshot.source_sequence = completed_current
        ? completed.source_sequence
        : producer.latest_sequence;
    snapshot.dropped_event_count = completed_current
        ? completed.source_dropped_event_count + completed.consumer_dropped_event_count
        : producer.dropped_event_count;
    snapshot.completed_raw_ns = completed_current
        ? completed.completed_raw_ns
        : starray::realtime::monotonic_now_ns();
    snapshot.session_anchor_raw_ns = producer.session_anchor_raw_ns;
    std::copy(
        projection.held_counts.begin(),
        projection.held_counts.end(),
        snapshot.touch_lane_held_counts);
    std::copy(
        projection.total_counts.begin(),
        projection.total_counts.end(),
        snapshot.touch_lane_total_counts);
    std::copy(
        projection.last_down_raw_ns.begin(),
        projection.last_down_raw_ns.end(),
        snapshot.touch_lane_last_down_raw_ns);
    std::copy(
        projection.last_up_raw_ns.begin(),
        projection.last_up_raw_ns.end(),
        snapshot.touch_lane_last_up_raw_ns);
    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

int modmanager_pccompat_read_clock_anchor_snapshot(void *output, uint32_t output_size) {
    if (output == nullptr || output_size < sizeof(PcCompatClockAnchorSnapshotV1))
        return -1;

    const auto *request = static_cast<const PcCompatClockAnchorSnapshotV1 *>(output);
    if (request->struct_size != sizeof(PcCompatClockAnchorSnapshotV1) ||
        request->abi_version != kClockAnchorSnapshotAbiVersion) {
        return -1;
    }

    starray::hud_logic::ClockAnchorSnapshot completed{};
    if (!starray::hud_logic::read_latest_clock_anchor(completed))
        return 0;
    if (request->publication_generation == completed.publication_generation)
        return 0;

    const PcCompatClockAnchorSnapshotV1 snapshot{
        .struct_size = sizeof(PcCompatClockAnchorSnapshotV1),
        .abi_version = kClockAnchorSnapshotAbiVersion,
        .publication_generation = completed.publication_generation,
        .session_generation = completed.session_generation,
        .valid_mask = completed.valid_mask,
        .frame_count = completed.frame_count,
        .unity_time_scale = completed.unity_time_scale,
        .audio_position_seconds = completed.audio_position_seconds,
        .unity_scaled_seconds = completed.unity_scaled_seconds,
        .song_position_seconds = completed.song_position_seconds,
        .map_position_seconds = completed.map_position_seconds,
        .monotonic_raw_ns = completed.monotonic_raw_ns,
    };
    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

int modmanager_pccompat_read_vm_fault_snapshot(void *output, uint32_t output_size) {
    if (output == nullptr || output_size < sizeof(PcCompatVmFaultSnapshotV1))
        return -1;

    const auto *request = static_cast<const PcCompatVmFaultSnapshotV1 *>(output);
    if (request->struct_size != sizeof(PcCompatVmFaultSnapshotV1) ||
        request->abi_version != kVmFaultSnapshotAbiVersion) {
        return -1;
    }

    starray::rule_vm::FaultRecord fault{};
    const auto read = starray::rule_vm::read_faults(request->cursor, &fault, 1);
    if (read.count == 0)
        return 0;

    PcCompatVmFaultSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    snapshot.abi_version = kVmFaultSnapshotAbiVersion;
    snapshot.cursor = read.cursor;
    snapshot.sequence = fault.sequence;
    snapshot.timestamp_ns = fault.timestamp_ns;
    snapshot.rule_id = fault.rule_id;
    snapshot.code = fault.code;
    snapshot.pc = fault.pc;
    snapshot.opcode = fault.opcode;
    snapshot.count = fault.count;
    snapshot.dropped_before_cursor = read.dropped_before_cursor;
    std::memcpy(snapshot.message, fault.message.data(), fault.message.size());
    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

const char *modmanager_pccompat_get_level_identity() {
    static thread_local std::string identity;
    std::lock_guard<std::mutex> guard(g_timeline_state_lock);
    identity = g_level_identity;
    return identity.c_str();
}

static int read_overlay_snapshot_current(void *output, uint32_t output_size) {
    if (output == nullptr || output_size < kOverlaySnapshotV2Size)
        return -1;

    const auto *request_words = static_cast<const uint32_t *>(output);
    const uint32_t requested_size = request_words[0];
    const uint32_t requested_abi = request_words[1];
    const uint32_t requested_generation = request_words[2];
    const bool wants_v7 =
        requested_size == sizeof(PcCompatOverlaySnapshotV1) &&
        requested_abi == kOverlaySnapshotAbiVersion &&
        output_size >= sizeof(PcCompatOverlaySnapshotV1);
    const bool wants_v6 =
        requested_size == kOverlaySnapshotV6Size &&
        requested_abi == kOverlaySnapshotAbiVersionV6 &&
        output_size >= kOverlaySnapshotV6Size;
    const bool wants_v5 =
        requested_size == kOverlaySnapshotV5Size &&
        requested_abi == kOverlaySnapshotAbiVersionV5 &&
        output_size >= kOverlaySnapshotV5Size;
    const bool wants_v4 =
        requested_size == kOverlaySnapshotV4Size &&
        requested_abi == kOverlaySnapshotAbiVersionV4 &&
        output_size >= kOverlaySnapshotV4Size;
    const bool wants_v3 =
        requested_size == kOverlaySnapshotV3Size &&
        requested_abi == kOverlaySnapshotAbiVersionV3 &&
        output_size >= kOverlaySnapshotV3Size;
    const bool wants_v2 =
        requested_size == kOverlaySnapshotV2Size &&
        requested_abi == kOverlaySnapshotAbiVersionV2;
    if (!wants_v7 && !wants_v6 && !wants_v5 && !wants_v4 && !wants_v3 && !wants_v2)
        return -1;

    const uint32_t current_generation =
        active_overlay_state().generation.load(std::memory_order_acquire);
    if (requested_generation == current_generation) {
        return 0;
    }

    PcCompatOverlaySnapshotV1 snapshot{};
    snapshot.struct_size = wants_v7
        ? static_cast<uint32_t>(sizeof(snapshot))
        : wants_v6
            ? kOverlaySnapshotV6Size
        : wants_v5
            ? kOverlaySnapshotV5Size
        : wants_v4
            ? kOverlaySnapshotV4Size
            : wants_v3 ? kOverlaySnapshotV3Size : kOverlaySnapshotV2Size;
    snapshot.abi_version = wants_v7
        ? kOverlaySnapshotAbiVersion
        : wants_v6
            ? kOverlaySnapshotAbiVersionV6
        : wants_v5
            ? kOverlaySnapshotAbiVersionV5
        : wants_v4
            ? kOverlaySnapshotAbiVersionV4
            : wants_v3 ? kOverlaySnapshotAbiVersionV3 : kOverlaySnapshotAbiVersionV2;

    for (int attempt = 0; attempt < 3; ++attempt) {
        const uint32_t generation_before =
            active_overlay_state().generation.load(std::memory_order_acquire);
        snapshot.visible = active_overlay_state().visible.load(std::memory_order_acquire);
        snapshot.practice = active_overlay_state().practice.load(std::memory_order_acquire);
        snapshot.show_count = active_overlay_state().show_count.load(std::memory_order_acquire);
        snapshot.hide_count = active_overlay_state().hide_count.load(std::memory_order_acquire);
        snapshot.player_update_count =
            active_overlay_state().player_update_count.load(std::memory_order_acquire);
        snapshot.state_change_count =
            active_overlay_state().state_change_count.load(std::memory_order_acquire);
        snapshot.last_op =
            static_cast<int32_t>(active_overlay_state().last_op.load(std::memory_order_acquire));
        snapshot.last_target_kind =
            static_cast<int32_t>(active_overlay_state().last_target_kind.load(std::memory_order_acquire));
        snapshot.player_count = active_overlay_state().player_count.load(std::memory_order_acquire);
        snapshot.last_seq_id = active_overlay_state().last_seq_id.load(std::memory_order_acquire);
        snapshot.last_is_restart =
            active_overlay_state().last_is_restart.load(std::memory_order_acquire);
        snapshot.last_wipe_direction =
            active_overlay_state().last_wipe_direction.load(std::memory_order_acquire);
        snapshot.last_reset_to_editor =
            active_overlay_state().last_reset_to_editor.load(std::memory_order_acquire);
        snapshot.judgement_hit_count =
            active_overlay_state().judgement_hit_count.load(std::memory_order_acquire);
        snapshot.judgement_reset_count =
            active_overlay_state().judgement_reset_count.load(std::memory_order_acquire);
        snapshot.last_hit_margin =
            active_overlay_state().last_hit_margin.load(std::memory_order_acquire);
        snapshot.floor_move_count =
            active_overlay_state().floor_move_count.load(std::memory_order_acquire);
        snapshot.last_floor_exit_angle = bits_to_float(
            active_overlay_state().last_floor_exit_angle_bits.load(std::memory_order_acquire));
        snapshot.last_floor_move_hit_margin =
            active_overlay_state().last_floor_move_hit_margin.load(std::memory_order_acquire);
        snapshot.player_hit_count =
            active_overlay_state().player_hit_count.load(std::memory_order_acquire);
        snapshot.last_player_hit_is_auto =
            active_overlay_state().last_player_hit_is_auto.load(std::memory_order_acquire);
        snapshot.death_count = active_overlay_state().death_count.load(std::memory_order_acquire);
        snapshot.last_death_overload =
            active_overlay_state().last_death_overload.load(std::memory_order_acquire);
        snapshot.last_death_multipress =
            active_overlay_state().last_death_multipress.load(std::memory_order_acquire);
        snapshot.last_death_hitbox =
            active_overlay_state().last_death_hitbox.load(std::memory_order_acquire);
        snapshot.hit_timing_count =
            active_overlay_state().hit_timing_count.load(std::memory_order_acquire);
        snapshot.last_hit_timing_ms = bits_to_float(
            active_overlay_state().last_hit_timing_ms_bits.load(std::memory_order_acquire));
        snapshot.last_hit_timing_margin =
            active_overlay_state().last_hit_timing_margin.load(std::memory_order_acquire);
        snapshot.accuracy_snapshot_count =
            active_overlay_state().accuracy_snapshot_count.load(std::memory_order_acquire);
        snapshot.percent_acc = bits_to_float(
            active_overlay_state().percent_acc_bits.load(std::memory_order_acquire));
        snapshot.percent_x_acc = bits_to_float(
            active_overlay_state().percent_x_acc_bits.load(std::memory_order_acquire));
        snapshot.progress = bits_to_float(
            active_overlay_state().progress_bits.load(std::memory_order_acquire));
        snapshot.combo_count = active_overlay_state().combo_count.load(std::memory_order_acquire);
        snapshot.attempt_count = active_overlay_state().attempt_count.load(std::memory_order_acquire);
        snapshot.bpm_snapshot_count =
            active_overlay_state().bpm_snapshot_count.load(std::memory_order_acquire);
        snapshot.tile_bpm = bits_to_float(
            active_overlay_state().tile_bpm_bits.load(std::memory_order_acquire));
        snapshot.kps = bits_to_float(
            active_overlay_state().kps_bits.load(std::memory_order_acquire));
        snapshot.timeline_snapshot_count =
            active_overlay_state().timeline_snapshot_count.load(std::memory_order_acquire);
        snapshot.music_time = bits_to_float(
            active_overlay_state().music_time_bits.load(std::memory_order_acquire));
        snapshot.music_total_time = bits_to_float(
            active_overlay_state().music_total_time_bits.load(std::memory_order_acquire));
        snapshot.map_time = bits_to_float(
            active_overlay_state().map_time_bits.load(std::memory_order_acquire));
        snapshot.map_total_time = bits_to_float(
            active_overlay_state().map_total_time_bits.load(std::memory_order_acquire));
        snapshot.checkpoints_used =
            active_overlay_state().checkpoints_used.load(std::memory_order_acquire);
        snapshot.current_checkpoint =
            active_overlay_state().current_checkpoint.load(std::memory_order_acquire);
        snapshot.total_checkpoints =
            active_overlay_state().total_checkpoints.load(std::memory_order_acquire);
        snapshot.current_seq_id =
            active_overlay_state().current_seq_id.load(std::memory_order_acquire);
        snapshot.floor_count =
            active_overlay_state().floor_count.load(std::memory_order_acquire);
        snapshot.start_progress = bits_to_float(
            active_overlay_state().start_progress_bits.load(std::memory_order_acquire));
        snapshot.speed_multiplier = bits_to_float(
            active_overlay_state().speed_multiplier_bits.load(std::memory_order_acquire));
        snapshot.session_auto =
            active_overlay_state().session_auto.load(std::memory_order_acquire);
        snapshot.input_state_generation =
            active_overlay_state().input_state_generation.load(std::memory_order_acquire);
        snapshot.input_held_mask =
            active_overlay_state().input_held_mask.load(std::memory_order_acquire);
        snapshot.input_last_down_mask =
            active_overlay_state().input_last_down_mask.load(std::memory_order_acquire);
        snapshot.input_last_up_mask =
            active_overlay_state().input_last_up_mask.load(std::memory_order_acquire);
        snapshot.input_total_count =
            active_overlay_state().input_total_count.load(std::memory_order_acquire);
        snapshot.input_kps = bits_to_float(
            active_overlay_state().input_kps_bits.load(std::memory_order_acquire));
        snapshot.planet_speed = bits_to_float(
            active_overlay_state().planet_speed_bits.load(std::memory_order_acquire));
        snapshot.rdc_auto = active_overlay_state().rdc_auto.load(std::memory_order_acquire);
        snapshot.no_fail = active_overlay_state().no_fail.load(std::memory_order_acquire);
        snapshot.paused = active_overlay_state().paused.load(std::memory_order_acquire);
        snapshot.is_game_world =
            active_overlay_state().is_game_world.load(std::memory_order_acquire);
        snapshot.song_pitch = bits_to_float(
            active_overlay_state().song_pitch_bits.load(std::memory_order_acquire));
        snapshot.conductor_add_offset = bits_to_double(
            active_overlay_state().conductor_add_offset_bits.load(std::memory_order_acquire));
        snapshot.conductor_songposition_minusi = bits_to_double(
            active_overlay_state().conductor_songposition_minusi_bits.load(
                std::memory_order_acquire));
        snapshot.is_scn_game =
            active_overlay_state().is_scn_game.load(std::memory_order_acquire);
        snapshot.game_ready =
            active_overlay_state().game_ready.load(std::memory_order_acquire);
        snapshot.session_epoch =
            active_overlay_state().session_epoch.load(std::memory_order_acquire);
        snapshot.valid_game_snapshot_fields =
            active_overlay_state().valid_game_snapshot_fields.load(std::memory_order_acquire);
        snapshot.controller_pointer = static_cast<uint64_t>(
            active_overlay_state().controller_pointer.load(std::memory_order_acquire));
        snapshot.conductor_pointer = static_cast<uint64_t>(
            active_overlay_state().conductor_pointer.load(std::memory_order_acquire));
        snapshot.level_maker_pointer = static_cast<uint64_t>(
            active_overlay_state().level_maker_pointer.load(std::memory_order_acquire));
        snapshot.current_floor_pointer = static_cast<uint64_t>(
            active_overlay_state().current_floor_pointer.load(std::memory_order_acquire));
        snapshot.first_floor_pointer = static_cast<uint64_t>(
            active_overlay_state().first_floor_pointer.load(std::memory_order_acquire));
        snapshot.song_pointer = static_cast<uint64_t>(
            active_overlay_state().song_pointer.load(std::memory_order_acquire));
        snapshot.planetary_system_pointer = static_cast<uint64_t>(
            active_overlay_state().planetary_system_pointer.load(std::memory_order_acquire));

        const uint32_t generation_after =
            active_overlay_state().generation.load(std::memory_order_acquire);
        snapshot.generation = generation_after;
        if (generation_before == generation_after)
            break;
    }

    std::memcpy(
        output,
        &snapshot,
        wants_v7
            ? sizeof(snapshot)
            : wants_v6
                ? kOverlaySnapshotV6Size
            : wants_v5
                ? kOverlaySnapshotV5Size
            : static_cast<size_t>(
                wants_v4
                    ? kOverlaySnapshotV4Size
                    : wants_v3 ? kOverlaySnapshotV3Size : kOverlaySnapshotV2Size));
    return 1;
}

int modmanager_pccompat_read_overlay_snapshot(void *output, uint32_t output_size) {
    const auto session = default_owner_overlay_session();
    OwnerOverlayScope scope(
        session != nullptr ? session.get() : &g_legacy_overlay_session);
    return read_overlay_snapshot_current(output, output_size);
}

int modmanager_pccompat_read_shared_game_snapshot(void *output, uint32_t output_size) {
    OwnerOverlayScope scope(&g_legacy_overlay_session);
    return read_overlay_snapshot_current(output, output_size);
}

int modmanager_pccompat_poll_shared_game_snapshot() {
    OwnerOverlayScope scope(&g_legacy_overlay_session);
    if (!poll_overlay_telemetry(nullptr, false))
        return 0;
    active_overlay_state().last_op.store(
        kRuleOpOverlayPollTelemetry,
        std::memory_order_release);
    active_overlay_state().generation.fetch_add(1, std::memory_order_acq_rel);
    return 1;
}

int modmanager_pccompat_read_overlay_snapshot_for_mod(
    const char *mod_id,
    void *output,
    uint32_t output_size) {
    const auto session = owner_overlay_session_for_mod(mod_id);
    if (session == nullptr ||
        session->retired.load(std::memory_order_acquire) != 0) {
        return -2;
    }
    OwnerOverlayScope scope(session.get());
    return read_overlay_snapshot_current(output, output_size);
}

int modmanager_pccompat_get_overlay_visible() {
    return any_owner_overlay_visible() ||
           active_overlay_state().visible.load(std::memory_order_acquire) != 0
        ? 1
        : 0;
}

int modmanager_pccompat_read_hit_margin_snapshot(void *output, uint32_t output_size) {
    if (output == nullptr || output_size != sizeof(PcCompatHitMarginSnapshotV1))
        return -1;

    // AddHit/Reset/SetPlayerCount publish synchronously on their hook paths. Keep a
    // throttled fallback for direct array mutations such as checkpoint rollback,
    // without repeating metadata/static-field/array work on every managed frame.
    const int64_t now_ms = steady_time_ms();
    const int64_t last_authoritative_ms =
        g_last_hit_margin_authoritative_publish_ms.load(std::memory_order_acquire);
    int64_t last_poll_ms =
        g_last_hit_margin_fallback_poll_ms.load(std::memory_order_acquire);
    const bool snapshot_valid =
        g_hit_margin_snapshot.valid.load(std::memory_order_acquire) != 0;
    const bool overlay_visible = any_owner_overlay_visible() ||
        active_overlay_state().visible.load(std::memory_order_acquire) != 0;
    if (overlay_visible &&
        (!snapshot_valid ||
         last_authoritative_ms == 0 ||
         now_ms - last_authoritative_ms >= kHitMarginFallbackPollIntervalMs) &&
        (last_poll_ms == 0 ||
         now_ms - last_poll_ms >= kHitMarginFallbackPollIntervalMs)) {
        if (g_last_hit_margin_fallback_poll_ms.compare_exchange_strong(
                last_poll_ms,
                now_ms,
                std::memory_order_acq_rel,
                std::memory_order_acquire)) {
            publish_hit_margin_snapshot(nullptr);
        }
    }

    PcCompatHitMarginSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(PcCompatHitMarginSnapshotV1);
    snapshot.abi_version = kHitMarginSnapshotAbiVersion;
    for (;;) {
        const uint32_t sequence_before =
            g_hit_margin_snapshot.sequence.load(std::memory_order_acquire);
        if ((sequence_before & 1u) != 0)
            continue;

        snapshot.generation =
            g_hit_margin_snapshot.generation.load(std::memory_order_relaxed);
        snapshot.valid = g_hit_margin_snapshot.valid.load(std::memory_order_relaxed);
        snapshot.length = g_hit_margin_snapshot.length.load(std::memory_order_relaxed);
        snapshot.checksum = g_hit_margin_snapshot.checksum.load(std::memory_order_relaxed);
        snapshot.tracker = static_cast<uint64_t>(
            g_hit_margin_snapshot.tracker.load(std::memory_order_relaxed));
        for (uint32_t index = 0; index < kHitMarginSnapshotMaxCounts; ++index) {
            snapshot.counts[index] =
                g_hit_margin_snapshot.counts[index].load(std::memory_order_relaxed);
        }

        const uint32_t sequence_after =
            g_hit_margin_snapshot.sequence.load(std::memory_order_acquire);
        if (sequence_before == sequence_after)
            break;
    }

    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

uintptr_t modmanager_pccompat_get_margin_tracker_instance() {
    return g_margin_tracker_instance.load(std::memory_order_acquire);
}

int modmanager_pccompat_get_overlay_practice() {
    return static_cast<int>(default_overlay_state_for_legacy_api().practice.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_show_count() {
    return default_overlay_state_for_legacy_api().show_count.load(std::memory_order_acquire);
}

uint32_t modmanager_pccompat_get_overlay_hide_count() {
    return default_overlay_state_for_legacy_api().hide_count.load(std::memory_order_acquire);
}

uint32_t modmanager_pccompat_get_overlay_player_update_count() {
    return default_overlay_state_for_legacy_api().player_update_count.load(std::memory_order_acquire);
}

uint32_t modmanager_pccompat_get_overlay_state_change_count() {
    return default_overlay_state_for_legacy_api().state_change_count.load(std::memory_order_acquire);
}

int modmanager_pccompat_get_overlay_last_op() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_op.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_target_kind() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_target_kind.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_player_count() {
    return static_cast<int>(default_overlay_state_for_legacy_api().player_count.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_seq_id() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_seq_id.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_is_restart() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_is_restart.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_wipe_direction() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_wipe_direction.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_reset_to_editor() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_reset_to_editor.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_judgement_hit_count() {
    return default_overlay_state_for_legacy_api().judgement_hit_count.load(std::memory_order_acquire);
}

uint32_t modmanager_pccompat_get_overlay_judgement_reset_count() {
    return default_overlay_state_for_legacy_api().judgement_reset_count.load(std::memory_order_acquire);
}

int modmanager_pccompat_get_overlay_last_hit_margin() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_hit_margin.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_floor_move_count() {
    return default_overlay_state_for_legacy_api().floor_move_count.load(std::memory_order_acquire);
}

float modmanager_pccompat_get_overlay_last_floor_exit_angle() {
    return bits_to_float(default_overlay_state_for_legacy_api().last_floor_exit_angle_bits.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_floor_move_hit_margin() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_floor_move_hit_margin.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_player_hit_count() {
    return default_overlay_state_for_legacy_api().player_hit_count.load(std::memory_order_acquire);
}

int modmanager_pccompat_get_overlay_last_player_hit_is_auto() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_player_hit_is_auto.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_death_count() {
    return default_overlay_state_for_legacy_api().death_count.load(std::memory_order_acquire);
}

int modmanager_pccompat_get_overlay_last_death_overload() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_death_overload.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_death_multipress() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_death_multipress.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_death_hitbox() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_death_hitbox.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_hit_timing_count() {
    return default_overlay_state_for_legacy_api().hit_timing_count.load(std::memory_order_acquire);
}

float modmanager_pccompat_get_overlay_last_hit_timing_ms() {
    return bits_to_float(default_overlay_state_for_legacy_api().last_hit_timing_ms_bits.load(std::memory_order_acquire));
}

int modmanager_pccompat_get_overlay_last_hit_timing_margin() {
    return static_cast<int>(default_overlay_state_for_legacy_api().last_hit_timing_margin.load(std::memory_order_acquire));
}

uint32_t modmanager_pccompat_get_overlay_accuracy_snapshot_count() {
    return default_overlay_state_for_legacy_api().accuracy_snapshot_count.load(std::memory_order_acquire);
}

float modmanager_pccompat_get_overlay_percent_acc() {
    return bits_to_float(default_overlay_state_for_legacy_api().percent_acc_bits.load(std::memory_order_acquire));
}

float modmanager_pccompat_get_overlay_percent_x_acc() {
    return bits_to_float(default_overlay_state_for_legacy_api().percent_x_acc_bits.load(std::memory_order_acquire));
}

} // extern "C"

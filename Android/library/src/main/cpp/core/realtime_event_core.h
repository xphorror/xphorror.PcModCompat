#pragma once

#include <cstddef>
#include <cstdint>
#include <array>

namespace starray::realtime {

constexpr size_t kCountJournalTouchProjectionCount = 5;
constexpr size_t kCountJournalMaxTouchLanes = 10;
constexpr size_t kCountJournalMaxKeyIdentities = 256;
constexpr size_t kLegacyInputKeyCapacity = 512;
constexpr size_t kLegacyInputHeldWordCount = kLegacyInputKeyCapacity / 64;
// Bounded producer journal. Native wake drains it independently from UnityMain;
// overflow remains explicit and never blocks the game's input producer.
constexpr size_t kRawInputEventJournalCapacity = 8192;
// Opens a consumer at the current ring tail without replaying pre-registration events.
constexpr uint64_t kOpenEventCursorAtTail = UINT64_MAX;
constexpr int32_t kDefaultTouchContactReuseDelayMs = 80;
constexpr int32_t kMaxTouchContactReuseDelayMs = 500;

constexpr uint32_t kExternalInputDeviceKeyboard = 1u << 0;
constexpr uint32_t kExternalInputDeviceController = 1u << 1;
constexpr uint32_t kExternalInputDeviceMouse = 1u << 2;
constexpr uint32_t kExternalInputDeviceMask =
    kExternalInputDeviceKeyboard |
    kExternalInputDeviceController |
    kExternalInputDeviceMouse;

enum class InputProducer : uint8_t {
    OfficialActivity = 1,
    AsyncInput = 2,
};

enum class InputSource : uint8_t {
    Touch = 1,
    Keyboard = 2,
    Controller = 3,
    Synthetic = 4,
    Mouse = 5,
    GameAction = 6,
};

enum class InputPhase : uint8_t {
    Down = 1,
    Up = 2,
    Cancel = 3,
    Reset = 4,
    ProducerChanged = 5,
};

enum class TouchLaneMappingMode : uint8_t {
    ScreenRegions = 0,
    TouchContacts = 1,
};

struct InputEvent {
    uint64_t sequence = 0;
    int64_t raw_ns = 0;
    uint32_t state_generation = 0;
    uint32_t session_generation = 0;
    uint32_t producer_epoch = 0;
    InputProducer producer = InputProducer::OfficialActivity;
    InputSource source = InputSource::Touch;
    InputPhase phase = InputPhase::Down;
    int32_t code = 0;
    int32_t slot = -1;
    int32_t pointer_count = 0;
    int32_t scan_code = 0;
    int32_t meta_state = 0;
    int32_t device_id = 0;
    int32_t repeat_count = 0;
    int32_t android_flags = 0;
    int32_t source_code = 0;
    int32_t viewport_width = 0;
    int32_t viewport_height = 0;
    float x = 0.0f;
    float y = 0.0f;
    uint32_t flags = 0;
};

struct InputSnapshot {
    uint64_t latest_sequence = 0;
    uint64_t dropped_event_count = 0;
    int64_t latest_raw_ns = 0;
    int64_t next_kps_expiry_ns = 0;
    int64_t session_anchor_raw_ns = 0;
    uint32_t generation = 0;
    uint32_t session_generation = 0;
    uint32_t producer_epoch = 1;
    InputProducer active_producer = InputProducer::OfficialActivity;
    uint32_t held_mask = 0;
    uint32_t last_down_mask = 0;
    uint32_t last_up_mask = 0;
    uint32_t total_count = 0;
    uint32_t keyboard_held_count = 0;
    float kps = 0.0f;
};

struct EventReadResult {
    size_t count = 0;
    uint64_t cursor = 0;
    uint64_t dropped_before_cursor = 0;
};

struct ExternalInputDeviceSnapshot {
    uint32_t abi_version = 1;
    uint32_t struct_size = sizeof(ExternalInputDeviceSnapshot);
    uint32_t generation = 0;
    uint32_t flags = 0;
};

// Accepted gameplay actions are intentionally separate from raw physical input.
// They have GameAction identity only, so consumers must not infer a keyboard key
// or touch lane from these events.
struct GameplayAcceptedEvent {
    uint64_t sequence = 0;
    int64_t raw_ns = 0;
    uint32_t session_generation = 0;
    uint32_t producer_epoch = 0;
    InputProducer producer = InputProducer::OfficialActivity;
    InputSource source = InputSource::GameAction;
    InputPhase phase = InputPhase::Down;
    int32_t input_event_state = 0;
    bool is_auto = false;
    bool is_test_macro = false;
};

struct GameplayAcceptedSnapshot {
    uint64_t latest_sequence = 0;
    uint64_t dropped_event_count = 0;
    int64_t latest_raw_ns = 0;
    uint32_t generation = 0;
    uint32_t session_generation = 0;
    uint32_t total_count = 0;
    uint32_t physical_count = 0;
    uint32_t synthetic_count = 0;
};

struct TouchCountJournalProjection {
    uint32_t lane_count = 0;
    uint32_t held_mask = 0;
    uint32_t last_down_mask = 0;
    uint32_t last_up_mask = 0;
    std::array<uint16_t, kCountJournalMaxTouchLanes> held_counts{};
    std::array<uint64_t, kCountJournalMaxTouchLanes> lifetime_down_counts{};
    std::array<uint64_t, kCountJournalMaxTouchLanes> session_down_counts{};
    std::array<int64_t, kCountJournalMaxTouchLanes> last_down_raw_ns{};
    std::array<int64_t, kCountJournalMaxTouchLanes> last_up_raw_ns{};
};

struct KeyCountJournalEntry {
    uint8_t occupied = 0;
    InputSource source = InputSource::Keyboard;
    int32_t code = 0;
    int32_t scan_code = 0;
    int32_t device_id = 0;
    uint64_t lifetime_down_count = 0;
    uint64_t session_down_count = 0;
    uint64_t latest_event_sequence = 0;
    int64_t latest_raw_ns = 0;
};

// This is a cumulative checkpoint, not another event queue. Values never lose
// accepted DOWN edges when the diagnostic/rain event ring overwrites old data.
// Session counters reset at begin_session(); lifetime counters only reset with
// the process (or reset_for_tests()).
struct InputCountJournalSnapshot {
    uint64_t generation = 0;
    uint64_t latest_event_sequence = 0;
    uint64_t lifetime_down_count = 0;
    uint64_t session_down_count = 0;
    uint64_t key_identity_overflow_count = 0;
    uint32_t session_generation = 0;
    uint32_t key_identity_count = 0;
    std::array<TouchCountJournalProjection,
               kCountJournalTouchProjectionCount> touch_projections{};
    std::array<KeyCountJournalEntry,
               kCountJournalMaxKeyIdentities> key_identities{};
};

struct LegacyInputSnapshot {
    uint32_t abi_version = 1;
    uint32_t struct_size = 0;
    uint64_t generation = 0;
    int64_t latest_raw_ns = 0;
    uint64_t keyboard_lifetime_down_count = 0;
    std::array<uint64_t, kLegacyInputHeldWordCount> held_words{};
    std::array<uint64_t, kLegacyInputKeyCapacity> down_ordinals{};
    std::array<uint64_t, kLegacyInputKeyCapacity> up_ordinals{};
};

int64_t monotonic_now_ns();

uint32_t set_active_producer(InputProducer producer,
                             int64_t raw_ns = 0);

InputProducer active_producer();

void cancel_touch_input(int64_t raw_ns = 0);

bool set_touch_lane_mapping_mode(TouchLaneMappingMode mode);

TouchLaneMappingMode touch_lane_mapping_mode();

bool set_touch_contact_reuse_delay_ms(int32_t milliseconds);

int32_t touch_contact_reuse_delay_ms();

bool observe_touch_raw(InputProducer producer,
                       int action,
                       int pointer_id,
                       int pointer_count,
                       int64_t raw_ns,
                       float x,
                       float y,
                       int viewport_width,
                       int viewport_height,
                       int source_code,
                       int device_id,
                       int android_flags);

bool observe_touch(int action,
                   int pointer_id,
                   int pointer_count,
                   int64_t event_time_ms,
                   float x,
                   float y,
                   int viewport_width,
                   int viewport_height);

bool observe_key_raw(InputProducer producer,
                     int action,
                     int key_code,
                     int scan_code,
                     int meta_state,
                     int device_id,
                     int repeat_count,
                     int64_t raw_ns,
                     int source_code,
                     int android_flags);

bool observe_key(int action,
                 int key_code,
                 int scan_code,
                 int meta_state,
                 int device_id,
                 int repeat_count,
                 int64_t event_time_ms,
                 int android_flags);

uint32_t begin_session(int64_t anchor_raw_ns = 0);

void set_external_input_devices(uint32_t flags);

ExternalInputDeviceSnapshot read_external_input_devices();

bool refresh_kps(int64_t now_ns);

InputSnapshot read_input_snapshot();

EventReadResult read_events(uint64_t cursor,
                            InputEvent *output,
                            size_t capacity);

bool observe_gameplay_accepted(bool is_auto,
                               int input_event_state,
                               int64_t raw_ns = 0,
                               bool is_test_macro = false);

GameplayAcceptedSnapshot read_gameplay_accepted_snapshot();

EventReadResult read_gameplay_accepted_events(
    uint64_t cursor,
    GameplayAcceptedEvent *output,
    size_t capacity);

InputCountJournalSnapshot read_count_journal_snapshot();

void read_input_checkpoint(InputSnapshot &input,
                           InputCountJournalSnapshot &count_journal);

bool read_legacy_input_snapshot(uint64_t known_generation,
                                LegacyInputSnapshot &snapshot);

void wait_for_change(uint64_t event_cursor,
                     uint32_t state_generation,
                     int64_t deadline_ns);

void notify_waiters();

void reset_for_tests();

}  // namespace starray::realtime

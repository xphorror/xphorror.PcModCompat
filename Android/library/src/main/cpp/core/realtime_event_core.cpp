#include "realtime_event_core.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <mutex>

namespace starray::realtime {
namespace {

constexpr size_t kMaxTouchSlots = 32;
constexpr size_t kMaxKeyboardSlots = 64;
constexpr size_t kGameplayAcceptedEventCapacity = 2048;
constexpr size_t kPressTimeCapacity = 512;
constexpr int64_t kKpsWindowNs = 1'000'000'000LL;
constexpr int32_t kAndroidSourceTouchscreen = 0x00001002;
constexpr int32_t kAndroidSourceMouse = 0x00002002;
constexpr uint32_t kEventFlagRepeat = 1u;
constexpr std::array<uint32_t, kCountJournalTouchProjectionCount>
    kCountJournalTouchLaneCounts{2, 4, 6, 8, 10};

struct EventRing {
    std::array<InputEvent, kRawInputEventJournalCapacity> values{};
    size_t head = 0;
    size_t count = 0;
    uint64_t next_sequence = 1;
    uint64_t dropped = 0;
};

struct PressTimeRing {
    std::array<int64_t, kPressTimeCapacity> values{};
    size_t head = 0;
    size_t count = 0;
};

struct GameplayAcceptedEventRing {
    std::array<GameplayAcceptedEvent, kGameplayAcceptedEventCapacity> values{};
    size_t head = 0;
    size_t count = 0;
    uint64_t next_sequence = 1;
    uint64_t dropped = 0;
};

struct KeyboardSlot {
    int32_t key_code = 0;
    int32_t scan_code = 0;
    int32_t device_id = 0;
    bool held = false;
};

struct RealtimeState {
    std::mutex lock;
    std::condition_variable changed;
    std::array<int32_t, kMaxTouchSlots> pointer_ids{};
    std::array<KeyboardSlot, kMaxKeyboardSlots> keyboard_slots{};
    EventRing events;
    PressTimeRing press_times;
    InputSnapshot snapshot;
    InputCountJournalSnapshot count_journal;
    LegacyInputSnapshot legacy_input;
    ExternalInputDeviceSnapshot external_input_devices;
    TouchLaneMappingMode touch_lane_mapping_mode =
        TouchLaneMappingMode::ScreenRegions;
    int32_t touch_contact_reuse_delay_ms =
        kDefaultTouchContactReuseDelayMs;
    std::array<std::array<int8_t, kMaxTouchSlots>,
               kCountJournalTouchProjectionCount> touch_contact_lanes{};
    uint64_t wake_generation = 0;

    RealtimeState() {
        pointer_ids.fill(-1);
        legacy_input.struct_size = sizeof(LegacyInputSnapshot);
        for (size_t index = 0; index < count_journal.touch_projections.size(); ++index) {
            count_journal.touch_projections[index].lane_count =
                kCountJournalTouchLaneCounts[index];
            touch_contact_lanes[index].fill(-1);
        }
    }
};

RealtimeState g_state;

struct GameplayAcceptedState {
    std::mutex lock;
    GameplayAcceptedEventRing events;
    GameplayAcceptedSnapshot snapshot;
};

GameplayAcceptedState g_gameplay_accepted_state;

int android_key_code_to_unity_key_code(int32_t key_code) {
    if (key_code >= 29 && key_code <= 54)
        return 97 + (key_code - 29);  // A..Z
    if (key_code >= 7 && key_code <= 16)
        return 48 + (key_code - 7);   // Alpha0..Alpha9
    if (key_code >= 131 && key_code <= 142)
        return 282 + (key_code - 131);  // F1..F12
    if (key_code >= 144 && key_code <= 153)
        return 256 + (key_code - 144);  // Keypad0..Keypad9

    switch (key_code) {
        case 4: return 27;    // Back/Escape
        case 19: return 273;  // UpArrow
        case 20: return 274;  // DownArrow
        case 21: return 276;  // LeftArrow
        case 22: return 275;  // RightArrow
        case 55: return 44;   // Comma
        case 56: return 46;   // Period
        case 57: return 308;  // LeftAlt
        case 58: return 307;  // RightAlt
        case 59: return 304;  // LeftShift
        case 60: return 303;  // RightShift
        case 61: return 9;    // Tab
        case 62: return 32;   // Space
        case 66: return 13;   // Return
        case 67: return 8;    // Backspace
        case 68: return 96;   // BackQuote
        case 69: return 45;   // Minus
        case 70: return 61;   // Equals
        case 71: return 91;   // LeftBracket
        case 72: return 93;   // RightBracket
        case 73: return 92;   // Backslash
        case 74: return 59;   // Semicolon
        case 75: return 39;   // Quote
        case 76: return 47;   // Slash
        case 81: return 43;   // Plus
        case 92: return 280;  // PageUp
        case 93: return 281;  // PageDown
        case 111: return 27;  // Escape
        case 112: return 127; // Delete
        case 113: return 306; // LeftControl
        case 114: return 305; // RightControl
        case 115: return 301; // CapsLock
        case 116: return 302; // ScrollLock
        case 122: return 278; // Home
        case 123: return 279; // End
        case 124: return 277; // Insert
        case 143: return 300; // Numlock
        case 154: return 267; // KeypadDivide
        case 155: return 268; // KeypadMultiply
        case 156: return 269; // KeypadMinus
        case 157: return 270; // KeypadPlus
        case 158: return 266; // KeypadPeriod
        case 160: return 271; // KeypadEnter
        case 161: return 272; // KeypadEquals
        default: return -1;
    }
}

void clear_legacy_input_held_locked(int64_t raw_ns) {
    bool changed = false;
    for (auto &word : g_state.legacy_input.held_words) {
        if (word != 0) {
            word = 0;
            changed = true;
        }
    }
    if (changed) {
        ++g_state.legacy_input.generation;
        g_state.legacy_input.latest_raw_ns = raw_ns;
    }
}

void record_legacy_input_event_locked(const InputEvent &event) {
    if (event.phase == InputPhase::Reset) {
        clear_legacy_input_held_locked(event.raw_ns);
        return;
    }
    if (event.source != InputSource::Keyboard)
        return;

    const int unity_key_code = android_key_code_to_unity_key_code(event.code);
    if (unity_key_code < 0 ||
        unity_key_code >= static_cast<int>(kLegacyInputKeyCapacity)) {
        return;
    }
    const size_t key_index = static_cast<size_t>(unity_key_code);
    const size_t word_index = key_index / 64;
    const uint64_t key_bit = 1ULL << (key_index % 64);
    const bool repeated = (event.flags & kEventFlagRepeat) != 0;

    if (event.phase == InputPhase::Down) {
        g_state.legacy_input.held_words[word_index] |= key_bit;
        if (!repeated) {
            ++g_state.legacy_input.down_ordinals[key_index];
            ++g_state.legacy_input.keyboard_lifetime_down_count;
        }
    } else if (event.phase == InputPhase::Up ||
               event.phase == InputPhase::Cancel) {
        g_state.legacy_input.held_words[word_index] &= ~key_bit;
        ++g_state.legacy_input.up_ordinals[key_index];
    } else {
        return;
    }

    ++g_state.legacy_input.generation;
    g_state.legacy_input.latest_raw_ns = event.raw_ns;
}

bool touch_lane_is_cooling_down(const TouchCountJournalProjection &projection,
                                size_t lane,
                                int64_t raw_ns,
                                int64_t delay_ns) {
    const int64_t last_up_raw_ns = projection.last_up_raw_ns[lane];
    return delay_ns > 0 &&
        last_up_raw_ns > 0 &&
        raw_ns >= last_up_raw_ns &&
        raw_ns - last_up_raw_ns < delay_ns;
}

int map_touch_contact_lane(const InputEvent &event,
                           const TouchCountJournalProjection &projection,
                           int32_t reuse_delay_ms) {
    const uint32_t lane_count = projection.lane_count;
    const int preferred_lane =
        event.slot >= 0 && event.slot < static_cast<int>(lane_count)
            ? event.slot
            : 0;
    const int64_t delay_ns = static_cast<int64_t>(reuse_delay_ms) * 1'000'000LL;

    for (uint32_t offset = 0; offset < lane_count; ++offset) {
        const size_t lane = static_cast<size_t>(
            (preferred_lane + static_cast<int>(offset)) %
            static_cast<int>(lane_count));
        if (projection.held_counts[lane] != 0 ||
            touch_lane_is_cooling_down(projection, lane, event.raw_ns, delay_ns)) {
            continue;
        }
        return static_cast<int>(lane);
    }

    int earliest_released_lane = -1;
    int64_t earliest_release_raw_ns = INT64_MAX;
    for (uint32_t offset = 0; offset < lane_count; ++offset) {
        const size_t lane = static_cast<size_t>(
            (preferred_lane + static_cast<int>(offset)) %
            static_cast<int>(lane_count));
        if (projection.held_counts[lane] != 0 ||
            projection.last_up_raw_ns[lane] >= earliest_release_raw_ns) {
            continue;
        }
        earliest_released_lane = static_cast<int>(lane);
        earliest_release_raw_ns = projection.last_up_raw_ns[lane];
    }
    return earliest_released_lane;
}

int map_touch_lane(const InputEvent &event,
                   const TouchCountJournalProjection &projection,
                   TouchLaneMappingMode mode,
                   int32_t reuse_delay_ms) {
    if (mode == TouchLaneMappingMode::TouchContacts) {
        return map_touch_contact_lane(event, projection, reuse_delay_ms);
    }
    if (event.viewport_width <= 0 || !std::isfinite(event.x))
        return 0;
    const double scaled =
        static_cast<double>(event.x) * static_cast<double>(projection.lane_count) /
        static_cast<double>(event.viewport_width);
    return std::clamp(
        static_cast<int>(std::floor(scaled)),
        0,
        static_cast<int>(projection.lane_count - 1));
}

void reset_touch_count_journal_session_locked() {
    for (size_t projection_index = 0;
         projection_index < g_state.count_journal.touch_projections.size();
         ++projection_index) {
        auto &projection =
            g_state.count_journal.touch_projections[projection_index];
        projection.held_mask = 0;
        projection.last_down_mask = 0;
        projection.last_up_mask = 0;
        projection.held_counts = {};
        projection.session_down_counts = {};
        projection.last_down_raw_ns = {};
        projection.last_up_raw_ns = {};
        g_state.touch_contact_lanes[projection_index].fill(-1);
    }
}

void release_touch_count_journal_contact_locked(size_t projection_index,
                                                size_t contact_slot,
                                                int64_t raw_ns) {
    auto &projection =
        g_state.count_journal.touch_projections[projection_index];
    auto &contact_lanes = g_state.touch_contact_lanes[projection_index];
    const int lane = contact_lanes[contact_slot];
    if (lane < 0 || lane >= static_cast<int>(projection.lane_count))
        return;

    const size_t lane_index = static_cast<size_t>(lane);
    auto &held_count = projection.held_counts[lane_index];
    if (held_count > 0)
        --held_count;
    const uint32_t lane_bit = 1u << static_cast<uint32_t>(lane);
    if (held_count == 0)
        projection.held_mask &= ~lane_bit;
    projection.last_up_mask = lane_bit;
    projection.last_up_raw_ns[lane_index] = raw_ns;
    contact_lanes[contact_slot] = -1;
}

void record_touch_count_journal_event_locked(const InputEvent &event) {
    if (event.source != InputSource::Touch)
        return;

    for (size_t projection_index = 0;
         projection_index < g_state.count_journal.touch_projections.size();
         ++projection_index) {
        auto &projection =
            g_state.count_journal.touch_projections[projection_index];
        auto &contact_lanes = g_state.touch_contact_lanes[projection_index];

        if (event.phase == InputPhase::Cancel) {
            uint32_t released_mask = 0;
            for (size_t slot = 0; slot < contact_lanes.size(); ++slot) {
                if ((event.flags & (1u << static_cast<uint32_t>(slot))) == 0)
                    continue;
                const int lane = contact_lanes[slot];
                if (lane >= 0 && lane < static_cast<int>(projection.lane_count))
                    released_mask |= 1u << static_cast<uint32_t>(lane);
                release_touch_count_journal_contact_locked(
                    projection_index,
                    slot,
                    event.raw_ns);
            }
            if (released_mask != 0)
                projection.last_up_mask = released_mask;
            continue;
        }

        if (event.slot < 0 ||
            event.slot >= static_cast<int>(contact_lanes.size())) {
            continue;
        }
        const size_t contact_slot = static_cast<size_t>(event.slot);
        if (event.phase == InputPhase::Down) {
            if (contact_lanes[contact_slot] >= 0)
                continue;
            const int lane = map_touch_lane(
                event,
                projection,
                g_state.touch_lane_mapping_mode,
                g_state.touch_contact_reuse_delay_ms);
            if (lane < 0 || lane >= static_cast<int>(projection.lane_count))
                continue;
            const size_t lane_index = static_cast<size_t>(lane);
            contact_lanes[contact_slot] = static_cast<int8_t>(lane);
            ++projection.held_counts[lane_index];
            ++projection.lifetime_down_counts[lane_index];
            ++projection.session_down_counts[lane_index];
            const uint32_t lane_bit = 1u << static_cast<uint32_t>(lane);
            projection.held_mask |= lane_bit;
            projection.last_down_mask = lane_bit;
            projection.last_down_raw_ns[lane_index] = event.raw_ns;
        } else if (event.phase == InputPhase::Up) {
            release_touch_count_journal_contact_locked(
                projection_index,
                contact_slot,
                event.raw_ns);
        }
    }
}

size_t key_count_journal_hash(InputSource source,
                              int32_t code,
                              int32_t scan_code,
                              int32_t device_id) {
    uint64_t value = static_cast<uint8_t>(source);
    value = (value * 1099511628211ULL) ^ static_cast<uint32_t>(code);
    value = (value * 1099511628211ULL) ^ static_cast<uint32_t>(scan_code);
    value = (value * 1099511628211ULL) ^ static_cast<uint32_t>(device_id);
    return static_cast<size_t>(value % kCountJournalMaxKeyIdentities);
}

KeyCountJournalEntry *find_or_allocate_key_count_journal_entry_locked(
    const InputEvent &event) {
    const size_t first = key_count_journal_hash(
        event.source,
        event.code,
        event.scan_code,
        event.device_id);
    for (size_t probe = 0; probe < kCountJournalMaxKeyIdentities; ++probe) {
        auto &entry = g_state.count_journal.key_identities[
            (first + probe) % kCountJournalMaxKeyIdentities];
        if (entry.occupied == 0) {
            entry.occupied = 1;
            entry.source = event.source;
            entry.code = event.code;
            entry.scan_code = event.scan_code;
            entry.device_id = event.device_id;
            ++g_state.count_journal.key_identity_count;
            return &entry;
        }
        if (entry.source == event.source &&
            entry.code == event.code &&
            entry.scan_code == event.scan_code &&
            entry.device_id == event.device_id) {
            return &entry;
        }
    }
    ++g_state.count_journal.key_identity_overflow_count;
    return nullptr;
}

void record_count_journal_event_locked(const InputEvent &event) {
    if (event.phase == InputPhase::Reset) {
        reset_touch_count_journal_session_locked();
        g_state.count_journal.session_down_count = 0;
        for (auto &entry : g_state.count_journal.key_identities)
            entry.session_down_count = 0;
        g_state.count_journal.session_generation = event.session_generation;
        ++g_state.count_journal.generation;
        g_state.count_journal.latest_event_sequence = event.sequence;
        return;
    }

    bool changed = false;
    record_touch_count_journal_event_locked(event);
    if (event.source == InputSource::Touch &&
        (event.phase == InputPhase::Down ||
         event.phase == InputPhase::Up ||
         event.phase == InputPhase::Cancel)) {
        changed = true;
    }
    const bool repeated_key =
        event.source == InputSource::Keyboard &&
        (event.flags & kEventFlagRepeat) != 0;
    if (event.phase != InputPhase::Down || repeated_key) {
        if (changed) {
            ++g_state.count_journal.generation;
            g_state.count_journal.latest_event_sequence = event.sequence;
        }
        return;
    }
    if (event.source != InputSource::Touch &&
        event.source != InputSource::Keyboard &&
        event.source != InputSource::Controller &&
        event.source != InputSource::Mouse) {
        if (changed) {
            ++g_state.count_journal.generation;
            g_state.count_journal.latest_event_sequence = event.sequence;
        }
        return;
    }

    changed = true;
    ++g_state.count_journal.lifetime_down_count;
    ++g_state.count_journal.session_down_count;

    if (event.source == InputSource::Keyboard ||
        event.source == InputSource::Controller ||
        event.source == InputSource::Mouse) {
        auto *entry = find_or_allocate_key_count_journal_entry_locked(event);
        if (entry != nullptr) {
            ++entry->lifetime_down_count;
            ++entry->session_down_count;
            entry->latest_event_sequence = event.sequence;
            entry->latest_raw_ns = event.raw_ns;
        }
    }
    if (changed) {
        ++g_state.count_journal.generation;
        g_state.count_journal.latest_event_sequence = event.sequence;
    }
}

void trim_press_times_locked(int64_t now_ns) {
    const int64_t cutoff = now_ns - kKpsWindowNs;
    auto &ring = g_state.press_times;
    while (ring.count > 0 && ring.values[ring.head] < cutoff) {
        ring.head = (ring.head + 1) % ring.values.size();
        --ring.count;
    }
    g_state.snapshot.next_kps_expiry_ns = ring.count > 0
        ? ring.values[ring.head] + kKpsWindowNs + 1
        : 0;
}

void push_press_time_locked(int64_t raw_ns) {
    auto &ring = g_state.press_times;
    if (ring.count == ring.values.size()) {
        ring.head = (ring.head + 1) % ring.values.size();
        --ring.count;
    }
    const size_t tail = (ring.head + ring.count) % ring.values.size();
    ring.values[tail] = raw_ns;
    ++ring.count;
    g_state.snapshot.next_kps_expiry_ns =
        ring.values[ring.head] + kKpsWindowNs + 1;
}

void append_event_locked(InputEvent event) {
    auto &ring = g_state.events;
    if (ring.count == ring.values.size()) {
        ring.head = (ring.head + 1) % ring.values.size();
        --ring.count;
        ++ring.dropped;
    }

    event.sequence = ring.next_sequence++;
    event.producer = g_state.snapshot.active_producer;
    event.producer_epoch = g_state.snapshot.producer_epoch;
    record_count_journal_event_locked(event);
    record_legacy_input_event_locked(event);
    const size_t tail = (ring.head + ring.count) % ring.values.size();
    ring.values[tail] = event;
    ++ring.count;

    g_state.snapshot.latest_sequence = event.sequence;
    g_state.snapshot.dropped_event_count = ring.dropped;
    g_state.snapshot.latest_raw_ns = event.raw_ns;
}

void append_gameplay_accepted_event_locked(GameplayAcceptedEvent event) {
    auto &state = g_gameplay_accepted_state;
    auto &ring = state.events;
    if (ring.count == ring.values.size()) {
        ring.head = (ring.head + 1) % ring.values.size();
        --ring.count;
        ++ring.dropped;
    }

    event.sequence = ring.next_sequence++;
    const size_t tail = (ring.head + ring.count) % ring.values.size();
    ring.values[tail] = event;
    ++ring.count;

    state.snapshot.latest_sequence = event.sequence;
    state.snapshot.dropped_event_count = ring.dropped;
    state.snapshot.latest_raw_ns = event.raw_ns;
    ++state.snapshot.generation;
    ++state.snapshot.total_count;
    if (event.is_auto || event.is_test_macro)
        ++state.snapshot.synthetic_count;
    else
        ++state.snapshot.physical_count;
}

int find_pointer_slot_locked(int pointer_id) {
    for (size_t index = 0; index < g_state.pointer_ids.size(); ++index) {
        if (g_state.pointer_ids[index] == pointer_id)
            return static_cast<int>(index);
    }
    return -1;
}

int allocate_pointer_slot_locked(int pointer_id) {
    for (size_t index = 0; index < g_state.pointer_ids.size(); ++index) {
        if (g_state.pointer_ids[index] < 0) {
            g_state.pointer_ids[index] = pointer_id;
            return static_cast<int>(index);
        }
    }
    return -1;
}

int find_keyboard_slot_locked(int key_code, int scan_code, int device_id) {
    for (size_t index = 0; index < g_state.keyboard_slots.size(); ++index) {
        const auto &slot = g_state.keyboard_slots[index];
        if (slot.held &&
            slot.key_code == key_code &&
            slot.scan_code == scan_code &&
            slot.device_id == device_id) {
            return static_cast<int>(index);
        }
    }
    return -1;
}

int allocate_keyboard_slot_locked(int key_code, int scan_code, int device_id) {
    for (size_t index = 0; index < g_state.keyboard_slots.size(); ++index) {
        auto &slot = g_state.keyboard_slots[index];
        if (!slot.held) {
            slot = KeyboardSlot{
                .key_code = key_code,
                .scan_code = scan_code,
                .device_id = device_id,
                .held = true,
            };
            return static_cast<int>(index);
        }
    }
    return -1;
}

}  // namespace

int64_t monotonic_now_ns() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

uint32_t set_active_producer(InputProducer producer, int64_t raw_ns) {
    if (raw_ns <= 0)
        raw_ns = monotonic_now_ns();

    std::lock_guard<std::mutex> guard(g_state.lock);
    if (g_state.snapshot.active_producer == producer)
        return g_state.snapshot.producer_epoch;

    const uint32_t released_touch_mask = g_state.snapshot.held_mask;
    if (released_touch_mask != 0) {
        append_event_locked(InputEvent{
            .raw_ns = raw_ns,
            .state_generation = g_state.snapshot.generation + 1,
            .session_generation = g_state.snapshot.session_generation,
            .source = InputSource::Touch,
            .phase = InputPhase::Cancel,
            .slot = -1,
            .flags = released_touch_mask,
        });
    }
    for (size_t index = 0; index < g_state.keyboard_slots.size(); ++index) {
        const auto &slot = g_state.keyboard_slots[index];
        if (!slot.held)
            continue;
        append_event_locked(InputEvent{
            .raw_ns = raw_ns,
            .state_generation = g_state.snapshot.generation + 1,
            .session_generation = g_state.snapshot.session_generation,
            .source = InputSource::Keyboard,
            .phase = InputPhase::Cancel,
            .code = slot.key_code,
            .slot = static_cast<int32_t>(index),
            .scan_code = slot.scan_code,
            .device_id = slot.device_id,
        });
    }

    g_state.pointer_ids.fill(-1);
    g_state.keyboard_slots = {};
    g_state.snapshot.held_mask = 0;
    g_state.snapshot.last_up_mask = released_touch_mask;
    g_state.snapshot.keyboard_held_count = 0;
    g_state.snapshot.active_producer = producer;
    ++g_state.snapshot.producer_epoch;
    ++g_state.snapshot.generation;

    append_event_locked(InputEvent{
        .raw_ns = raw_ns,
        .state_generation = g_state.snapshot.generation,
        .session_generation = g_state.snapshot.session_generation,
        .source = InputSource::Synthetic,
        .phase = InputPhase::ProducerChanged,
    });
    g_state.changed.notify_all();
    return g_state.snapshot.producer_epoch;
}

InputProducer active_producer() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    return g_state.snapshot.active_producer;
}

void cancel_touch_input(int64_t raw_ns) {
    if (raw_ns <= 0)
        raw_ns = monotonic_now_ns();

    std::lock_guard<std::mutex> guard(g_state.lock);
    const uint32_t released_touch_mask = g_state.snapshot.held_mask;
    g_state.pointer_ids.fill(-1);
    if (released_touch_mask == 0)
        return;

    append_event_locked(InputEvent{
        .raw_ns = raw_ns,
        .state_generation = g_state.snapshot.generation + 1,
        .session_generation = g_state.snapshot.session_generation,
        .source = InputSource::Touch,
        .phase = InputPhase::Cancel,
        .slot = -1,
        .flags = released_touch_mask,
    });
    g_state.snapshot.held_mask = 0;
    g_state.snapshot.last_up_mask = released_touch_mask;
    ++g_state.snapshot.generation;
    g_state.changed.notify_all();
}

bool set_touch_lane_mapping_mode(TouchLaneMappingMode mode) {
    if (mode != TouchLaneMappingMode::ScreenRegions &&
        mode != TouchLaneMappingMode::TouchContacts) {
        return false;
    }
    std::lock_guard<std::mutex> guard(g_state.lock);
    if (g_state.touch_lane_mapping_mode == mode)
        return true;
    g_state.touch_lane_mapping_mode = mode;
    for (size_t index = 0;
         index < g_state.count_journal.touch_projections.size();
         ++index) {
        auto &projection = g_state.count_journal.touch_projections[index];
        const uint32_t lane_count = projection.lane_count;
        projection = {};
        projection.lane_count = lane_count;
        g_state.touch_contact_lanes[index].fill(-1);
    }
    ++g_state.count_journal.generation;
    ++g_state.wake_generation;
    g_state.changed.notify_all();
    return true;
}

TouchLaneMappingMode touch_lane_mapping_mode() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    return g_state.touch_lane_mapping_mode;
}

bool set_touch_contact_reuse_delay_ms(int32_t milliseconds) {
    if (milliseconds < 0 || milliseconds > kMaxTouchContactReuseDelayMs)
        return false;
    std::lock_guard<std::mutex> guard(g_state.lock);
    g_state.touch_contact_reuse_delay_ms = milliseconds;
    return true;
}

int32_t touch_contact_reuse_delay_ms() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    return g_state.touch_contact_reuse_delay_ms;
}

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
                       int android_flags) {
    constexpr int kActionDown = 0;
    constexpr int kActionUp = 1;
    constexpr int kActionCancel = 3;
    constexpr int kActionPointerDown = 5;
    constexpr int kActionPointerUp = 6;

    if (pointer_id < 0 && action != kActionCancel)
        return false;

    if (raw_ns <= 0)
        raw_ns = monotonic_now_ns();

    std::lock_guard<std::mutex> guard(g_state.lock);
    if (g_state.snapshot.active_producer != producer)
        return false;
    int slot = find_pointer_slot_locked(pointer_id);
    InputPhase phase = InputPhase::Down;
    uint32_t edge_mask = 0;
    bool changed = false;

    if (action == kActionDown || action == kActionPointerDown) {
        if (slot >= 0)
            return false;
        slot = allocate_pointer_slot_locked(pointer_id);
        if (slot >= 0) {
            edge_mask = 1u << static_cast<uint32_t>(slot);
            g_state.snapshot.held_mask |= edge_mask;
            g_state.snapshot.last_down_mask = edge_mask;
            ++g_state.snapshot.total_count;
            trim_press_times_locked(raw_ns);
            push_press_time_locked(raw_ns);
            phase = InputPhase::Down;
            changed = true;
        }
    } else if (action == kActionUp || action == kActionPointerUp) {
        if (slot >= 0) {
            edge_mask = 1u << static_cast<uint32_t>(slot);
            g_state.snapshot.held_mask &= ~edge_mask;
            g_state.snapshot.last_up_mask = edge_mask;
            g_state.pointer_ids[static_cast<size_t>(slot)] = -1;
            phase = InputPhase::Up;
            changed = true;
        }
    } else if (action == kActionCancel) {
        // pointer_id may be -1 for a global cancel; find_pointer_slot_locked(-1)
        // would otherwise match the first *empty* slot and publish a bogus index.
        slot = pointer_id >= 0 ? slot : -1;
        if (g_state.snapshot.held_mask != 0) {
            edge_mask = g_state.snapshot.held_mask;
            g_state.snapshot.last_up_mask = edge_mask;
            g_state.snapshot.held_mask = 0;
            phase = InputPhase::Cancel;
            changed = true;
        }
        g_state.pointer_ids.fill(-1);
    }

    if (!changed)
        return false;

    trim_press_times_locked(raw_ns);
    g_state.snapshot.kps = static_cast<float>(g_state.press_times.count);
    ++g_state.snapshot.generation;

    append_event_locked(InputEvent{
        .raw_ns = raw_ns,
        .state_generation = g_state.snapshot.generation,
        .session_generation = g_state.snapshot.session_generation,
        .source = source_code == kAndroidSourceMouse
            ? InputSource::Mouse
            : InputSource::Touch,
        .phase = phase,
        .code = pointer_id,
        .slot = slot,
        .pointer_count = pointer_count,
        .device_id = device_id,
        .android_flags = android_flags,
        .source_code = source_code,
        .viewport_width = std::max(viewport_width, 0),
        .viewport_height = std::max(viewport_height, 0),
        .x = std::isfinite(x) ? x : 0.0f,
        .y = std::isfinite(y) ? y : 0.0f,
        .flags = edge_mask,
    });
    g_state.changed.notify_all();
    return true;
}

bool observe_touch(int action,
                   int pointer_id,
                   int pointer_count,
                   int64_t event_time_ms,
                   float x,
                   float y,
                   int viewport_width,
                   int viewport_height) {
    return observe_touch_raw(
        InputProducer::OfficialActivity,
        action,
        pointer_id,
        pointer_count,
        event_time_ms > 0 ? event_time_ms * 1'000'000LL : 0,
        x,
        y,
        viewport_width,
        viewport_height,
        kAndroidSourceTouchscreen,
        0,
        0);
}

bool observe_key_raw(InputProducer producer,
                     int action,
                     int key_code,
                     int scan_code,
                     int meta_state,
                     int device_id,
                     int repeat_count,
                     int64_t raw_ns,
                     int source_code,
                     int android_flags) {
    constexpr int kActionDown = 0;
    constexpr int kActionUp = 1;
    constexpr int kAndroidKeyFlagCanceled = 0x20;

    if (key_code <= 0 || (action != kActionDown && action != kActionUp))
        return false;

    if (raw_ns <= 0)
        raw_ns = monotonic_now_ns();

    std::lock_guard<std::mutex> guard(g_state.lock);
    if (g_state.snapshot.active_producer != producer)
        return false;
    int slot = find_keyboard_slot_locked(key_code, scan_code, device_id);
    InputPhase phase = InputPhase::Down;
    bool state_changed = false;
    bool repeated = false;

    if (action == kActionDown) {
        repeated = repeat_count > 0 || slot >= 0;
        if (slot < 0) {
            slot = allocate_keyboard_slot_locked(key_code, scan_code, device_id);
            if (slot < 0)
                return false;
            ++g_state.snapshot.keyboard_held_count;
            state_changed = true;
            if (repeat_count <= 0) {
                ++g_state.snapshot.total_count;
                trim_press_times_locked(raw_ns);
                push_press_time_locked(raw_ns);
            }
        }
    } else {
        if (slot < 0)
            return false;
        g_state.keyboard_slots[static_cast<size_t>(slot)] = {};
        if (g_state.snapshot.keyboard_held_count > 0)
            --g_state.snapshot.keyboard_held_count;
        phase = (android_flags & kAndroidKeyFlagCanceled) != 0
            ? InputPhase::Cancel
            : InputPhase::Up;
        state_changed = true;
    }

    trim_press_times_locked(raw_ns);
    g_state.snapshot.kps = static_cast<float>(g_state.press_times.count);
    if (state_changed)
        ++g_state.snapshot.generation;

    append_event_locked(InputEvent{
        .raw_ns = raw_ns,
        .state_generation = g_state.snapshot.generation,
        .session_generation = g_state.snapshot.session_generation,
        .source = InputSource::Keyboard,
        .phase = phase,
        .code = key_code,
        .slot = slot,
        .pointer_count = 0,
        .scan_code = scan_code,
        .meta_state = meta_state,
        .device_id = device_id,
        .repeat_count = repeat_count,
        .android_flags = android_flags,
        .source_code = source_code,
        .flags = repeated ? kEventFlagRepeat : 0u,
    });
    g_state.changed.notify_all();
    return true;
}

bool observe_key(int action,
                 int key_code,
                 int scan_code,
                 int meta_state,
                 int device_id,
                 int repeat_count,
                 int64_t event_time_ms,
                 int android_flags) {
    return observe_key_raw(
        InputProducer::OfficialActivity,
        action,
        key_code,
        scan_code,
        meta_state,
        device_id,
        repeat_count,
        event_time_ms > 0 ? event_time_ms * 1'000'000LL : 0,
        0x00000101,
        android_flags);
}

uint32_t begin_session(int64_t anchor_raw_ns) {
    if (anchor_raw_ns <= 0)
        anchor_raw_ns = monotonic_now_ns();

    uint32_t session_generation = 0;
    {
        std::lock_guard<std::mutex> guard(g_state.lock);
        g_state.pointer_ids.fill(-1);
        g_state.keyboard_slots = {};
        g_state.press_times = {};
        g_state.snapshot.held_mask = 0;
        g_state.snapshot.last_down_mask = 0;
        g_state.snapshot.last_up_mask = 0;
        g_state.snapshot.total_count = 0;
        g_state.snapshot.keyboard_held_count = 0;
        g_state.snapshot.kps = 0.0f;
        g_state.snapshot.next_kps_expiry_ns = 0;
        g_state.snapshot.latest_raw_ns = anchor_raw_ns;
        g_state.snapshot.session_anchor_raw_ns = anchor_raw_ns;
        ++g_state.snapshot.session_generation;
        ++g_state.snapshot.generation;
        session_generation = g_state.snapshot.session_generation;

        append_event_locked(InputEvent{
            .raw_ns = anchor_raw_ns,
            .state_generation = g_state.snapshot.generation,
            .session_generation = session_generation,
            .source = InputSource::Synthetic,
            .phase = InputPhase::Reset,
            .flags = g_state.external_input_devices.flags,
        });
        g_state.changed.notify_all();
    }

    {
        std::lock_guard<std::mutex> guard(g_gameplay_accepted_state.lock);
        auto &snapshot = g_gameplay_accepted_state.snapshot;
        snapshot.latest_raw_ns = anchor_raw_ns;
        snapshot.session_generation = session_generation;
        snapshot.total_count = 0;
        snapshot.physical_count = 0;
        snapshot.synthetic_count = 0;
        ++snapshot.generation;
    }
    return session_generation;
}

void set_external_input_devices(uint32_t flags) {
    flags &= kExternalInputDeviceMask;
    std::lock_guard<std::mutex> guard(g_state.lock);
    if (g_state.external_input_devices.flags == flags)
        return;
    g_state.external_input_devices.flags = flags;
    ++g_state.external_input_devices.generation;
}

ExternalInputDeviceSnapshot read_external_input_devices() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    return g_state.external_input_devices;
}

bool refresh_kps(int64_t now_ns) {
    if (now_ns <= 0)
        now_ns = monotonic_now_ns();

    std::lock_guard<std::mutex> guard(g_state.lock);
    trim_press_times_locked(now_ns);
    const float next_kps = static_cast<float>(g_state.press_times.count);
    if (g_state.snapshot.kps == next_kps)
        return false;

    g_state.snapshot.kps = next_kps;
    g_state.snapshot.latest_raw_ns = std::max(g_state.snapshot.latest_raw_ns, now_ns);
    ++g_state.snapshot.generation;
    g_state.changed.notify_all();
    return true;
}

InputSnapshot read_input_snapshot() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    return g_state.snapshot;
}

InputCountJournalSnapshot read_count_journal_snapshot() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    return g_state.count_journal;
}

void read_input_checkpoint(InputSnapshot &input,
                           InputCountJournalSnapshot &count_journal) {
    std::lock_guard<std::mutex> guard(g_state.lock);
    input = g_state.snapshot;
    count_journal = g_state.count_journal;
}

bool read_legacy_input_snapshot(uint64_t known_generation,
                                LegacyInputSnapshot &snapshot) {
    std::lock_guard<std::mutex> guard(g_state.lock);
    if (known_generation == g_state.legacy_input.generation)
        return false;
    snapshot = g_state.legacy_input;
    return true;
}

EventReadResult read_events(uint64_t cursor,
                            InputEvent *output,
                            size_t capacity) {
    std::lock_guard<std::mutex> guard(g_state.lock);
    EventReadResult result{};
    result.cursor = cursor;
    if (cursor == kOpenEventCursorAtTail) {
        const auto &ring = g_state.events;
        result.cursor = ring.count == 0
            ? 0
            : ring.values[(ring.head + ring.count - 1) % ring.values.size()].sequence;
        return result;
    }
    if (output == nullptr || capacity == 0 || g_state.events.count == 0)
        return result;

    const auto &ring = g_state.events;
    const uint64_t oldest_sequence = ring.values[ring.head].sequence;
    uint64_t requested_sequence = cursor + 1;
    if (requested_sequence < oldest_sequence) {
        result.dropped_before_cursor = oldest_sequence - requested_sequence;
        requested_sequence = oldest_sequence;
    }

    const uint64_t newest_sequence =
        ring.values[(ring.head + ring.count - 1) % ring.values.size()].sequence;
    if (requested_sequence > newest_sequence)
        return result;
    const size_t first_offset = static_cast<size_t>(requested_sequence - oldest_sequence);
    for (size_t offset = first_offset;
         offset < ring.count && result.count < capacity;
         ++offset) {
        const auto &event = ring.values[(ring.head + offset) % ring.values.size()];
        output[result.count++] = event;
        result.cursor = event.sequence;
    }
    return result;
}

bool observe_gameplay_accepted(bool is_auto,
                               int input_event_state,
                               int64_t raw_ns,
                               bool is_test_macro) {
    constexpr int kInputEventStateDown = 0;
    constexpr int kInputEventStateUp = 1;
    if (input_event_state != kInputEventStateDown &&
        input_event_state != kInputEventStateUp) {
        return false;
    }
    if (raw_ns <= 0)
        raw_ns = monotonic_now_ns();

    InputProducer producer;
    uint32_t producer_epoch;
    uint32_t session_generation;
    {
        std::lock_guard<std::mutex> guard(g_state.lock);
        producer = g_state.snapshot.active_producer;
        producer_epoch = g_state.snapshot.producer_epoch;
        session_generation = g_state.snapshot.session_generation;
    }

    std::lock_guard<std::mutex> guard(g_gameplay_accepted_state.lock);
    append_gameplay_accepted_event_locked(GameplayAcceptedEvent{
        .raw_ns = raw_ns,
        .session_generation = session_generation,
        .producer_epoch = producer_epoch,
        .producer = producer,
        .source = is_auto || is_test_macro
            ? InputSource::Synthetic
            : InputSource::GameAction,
        .phase = input_event_state == kInputEventStateDown
            ? InputPhase::Down
            : InputPhase::Up,
        .input_event_state = input_event_state,
        .is_auto = is_auto,
        .is_test_macro = is_test_macro,
    });
    return true;
}

GameplayAcceptedSnapshot read_gameplay_accepted_snapshot() {
    std::lock_guard<std::mutex> guard(g_gameplay_accepted_state.lock);
    return g_gameplay_accepted_state.snapshot;
}

EventReadResult read_gameplay_accepted_events(
    uint64_t cursor,
    GameplayAcceptedEvent *output,
    size_t capacity) {
    std::lock_guard<std::mutex> guard(g_gameplay_accepted_state.lock);
    EventReadResult result{};
    result.cursor = cursor;
    const auto &ring = g_gameplay_accepted_state.events;
    if (output == nullptr || capacity == 0 || ring.count == 0)
        return result;

    const uint64_t oldest_sequence = ring.values[ring.head].sequence;
    uint64_t requested_sequence = cursor + 1;
    if (requested_sequence < oldest_sequence) {
        result.dropped_before_cursor = oldest_sequence - requested_sequence;
        requested_sequence = oldest_sequence;
    }

    for (size_t offset = 0; offset < ring.count && result.count < capacity; ++offset) {
        const auto &event = ring.values[(ring.head + offset) % ring.values.size()];
        if (event.sequence < requested_sequence)
            continue;
        output[result.count++] = event;
        result.cursor = event.sequence;
    }
    return result;
}

void wait_for_change(uint64_t event_cursor,
                     uint32_t state_generation,
                     int64_t deadline_ns) {
    std::unique_lock<std::mutex> lock(g_state.lock);
    const auto wake_generation = g_state.wake_generation;
    const auto changed = [event_cursor, state_generation, wake_generation] {
        return g_state.snapshot.latest_sequence > event_cursor ||
            g_state.snapshot.generation != state_generation ||
            g_state.wake_generation != wake_generation;
    };
    if (changed())
        return;

    if (deadline_ns <= 0) {
        g_state.changed.wait(lock, changed);
        return;
    }

    const auto deadline = std::chrono::steady_clock::time_point(
        std::chrono::nanoseconds(deadline_ns));
    g_state.changed.wait_until(lock, deadline, changed);
}

void notify_waiters() {
    std::lock_guard<std::mutex> guard(g_state.lock);
    ++g_state.wake_generation;
    g_state.changed.notify_all();
}

void reset_for_tests() {
    {
        std::lock_guard<std::mutex> guard(g_state.lock);
        g_state.pointer_ids.fill(-1);
        g_state.keyboard_slots = {};
        g_state.events = {};
        g_state.press_times = {};
        g_state.snapshot = {};
        g_state.count_journal = {};
        g_state.legacy_input = {};
        g_state.touch_lane_mapping_mode = TouchLaneMappingMode::ScreenRegions;
        g_state.touch_contact_reuse_delay_ms =
            kDefaultTouchContactReuseDelayMs;
        g_state.legacy_input.struct_size = sizeof(LegacyInputSnapshot);
        for (size_t index = 0;
             index < g_state.count_journal.touch_projections.size();
             ++index) {
            g_state.count_journal.touch_projections[index].lane_count =
                kCountJournalTouchLaneCounts[index];
            g_state.touch_contact_lanes[index].fill(-1);
        }
        ++g_state.wake_generation;
        g_state.changed.notify_all();
    }
    {
        std::lock_guard<std::mutex> guard(g_gameplay_accepted_state.lock);
        g_gameplay_accepted_state.events = {};
        g_gameplay_accepted_state.snapshot = {};
    }
}

}  // namespace starray::realtime

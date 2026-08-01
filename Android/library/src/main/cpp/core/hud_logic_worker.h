#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

namespace starray::hud_logic {

constexpr size_t kMaxTouchLanes = 10;
constexpr size_t kTouchLaneProjectionCount = 5;

enum ClockAnchorValid : uint32_t {
    ClockAnchorUnityScaled = 1u << 0,
    ClockAnchorSongPosition = 1u << 1,
    ClockAnchorAudioPosition = 1u << 2,
    ClockAnchorMapPosition = 1u << 3,
    ClockAnchorFrameCount = 1u << 4,
};

struct ClockAnchorSnapshot {
    uint32_t available = 0;
    uint32_t publication_generation = 0;
    uint32_t session_generation = 0;
    uint32_t valid_mask = 0;
    int32_t frame_count = 0;
    float unity_time_scale = 1.0f;
    float audio_position_seconds = 0.0f;
    double unity_scaled_seconds = 0.0;
    double song_position_seconds = 0.0;
    double map_position_seconds = 0.0;
    int64_t monotonic_raw_ns = 0;
};

struct TouchLaneProjectionSnapshot {
    uint32_t lane_count = 0;
    uint32_t held_mask = 0;
    uint32_t last_down_mask = 0;
    uint32_t last_up_mask = 0;
    std::array<uint16_t, kMaxTouchLanes> held_counts{};
    std::array<uint32_t, kMaxTouchLanes> total_counts{};
    std::array<int64_t, kMaxTouchLanes> last_down_raw_ns{};
    std::array<int64_t, kMaxTouchLanes> last_up_raw_ns{};
};

struct CompletedInputSnapshot {
    uint32_t available = 0;
    uint32_t publication_generation = 0;
    uint32_t source_generation = 0;
    uint32_t session_generation = 0;
    uint32_t held_mask = 0;
    uint32_t last_down_mask = 0;
    uint32_t last_up_mask = 0;
    uint32_t total_count = 0;
    uint32_t keyboard_held_count = 0;
    uint32_t touch_lane_count = 0;
    uint32_t touch_lane_held_mask = 0;
    uint32_t touch_lane_last_down_mask = 0;
    uint32_t touch_lane_last_up_mask = 0;
    float kps = 0.0f;
    uint64_t source_sequence = 0;
    uint64_t source_dropped_event_count = 0;
    uint64_t consumer_dropped_event_count = 0;
    int64_t completed_raw_ns = 0;
    int64_t session_anchor_raw_ns = 0;
    std::array<uint16_t, kMaxTouchLanes> touch_lane_held_counts{};
    std::array<uint32_t, kMaxTouchLanes> touch_lane_total_counts{};
    std::array<int64_t, kMaxTouchLanes> touch_lane_last_down_raw_ns{};
    std::array<int64_t, kMaxTouchLanes> touch_lane_last_up_raw_ns{};
    std::array<TouchLaneProjectionSnapshot, kTouchLaneProjectionCount> touch_lane_projections{};
};

void ensure_started();

bool read_latest_input_snapshot(CompletedInputSnapshot &snapshot);

bool select_touch_lane_projection(const CompletedInputSnapshot &snapshot,
                                  uint32_t lane_count,
                                  TouchLaneProjectionSnapshot &projection);

void publish_clock_anchor(ClockAnchorSnapshot anchor);

bool read_latest_clock_anchor(ClockAnchorSnapshot &anchor);

}  // namespace starray::hud_logic

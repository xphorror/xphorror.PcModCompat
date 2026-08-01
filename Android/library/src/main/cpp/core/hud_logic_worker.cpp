#include "hud_logic_worker.h"

#include "hud_deadline_scheduler.h"
#include "realtime_event_core.h"
#include "ui_recipe_lifecycle_runtime.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <mutex>
#include <thread>
#include <type_traits>

namespace starray::hud_logic {
namespace {

constexpr size_t kCompletedSnapshotHistory = 3;
constexpr size_t kEventBatchSize = 64;

std::mutex g_snapshot_lock;
std::array<CompletedInputSnapshot, kCompletedSnapshotHistory> g_snapshot_history{};
size_t g_snapshot_count = 0;
size_t g_snapshot_write_index = 0;
uint32_t g_publication_generation = 0;
std::mutex g_clock_anchor_lock;
std::array<ClockAnchorSnapshot, kCompletedSnapshotHistory> g_clock_anchor_history{};
size_t g_clock_anchor_count = 0;
size_t g_clock_anchor_write_index = 0;
uint32_t g_clock_anchor_publication_generation = 0;
std::once_flag g_worker_once;

static_assert(std::is_trivially_copyable_v<CompletedInputSnapshot>);
static_assert(std::is_trivially_copyable_v<ClockAnchorSnapshot>);

uint32_t saturating_u32(uint64_t value) {
    return value > std::numeric_limits<uint32_t>::max()
        ? std::numeric_limits<uint32_t>::max()
        : static_cast<uint32_t>(value);
}

CompletedInputSnapshot publish_snapshot(const realtime::InputSnapshot &source,
                                        const realtime::InputCountJournalSnapshot &journal,
                                        uint64_t event_cursor,
                                        uint64_t consumer_dropped) {
    std::lock_guard<std::mutex> guard(g_snapshot_lock);
    const uint32_t publication = ++g_publication_generation;
    std::array<TouchLaneProjectionSnapshot, kTouchLaneProjectionCount> projections{};
    for (size_t index = 0; index < projections.size(); ++index) {
        const auto &source_projection = journal.touch_projections[index];
        auto &projection = projections[index];
        projection.lane_count = source_projection.lane_count;
        projection.held_mask = source_projection.held_mask;
        projection.last_down_mask = source_projection.last_down_mask;
        projection.last_up_mask = source_projection.last_up_mask;
        projection.held_counts = source_projection.held_counts;
        projection.last_down_raw_ns = source_projection.last_down_raw_ns;
        projection.last_up_raw_ns = source_projection.last_up_raw_ns;
        for (size_t lane = 0; lane < projection.total_counts.size(); ++lane) {
            projection.total_counts[lane] =
                saturating_u32(source_projection.session_down_counts[lane]);
        }
    }
    const auto &default_projection = projections.back();
    const CompletedInputSnapshot completed{
        .available = 1,
        .publication_generation = publication,
        .source_generation = source.generation,
        .session_generation = source.session_generation,
        .held_mask = source.held_mask,
        .last_down_mask = source.last_down_mask,
        .last_up_mask = source.last_up_mask,
        .total_count = source.total_count,
        .keyboard_held_count = source.keyboard_held_count,
        .touch_lane_count = default_projection.lane_count,
        .touch_lane_held_mask = default_projection.held_mask,
        .touch_lane_last_down_mask = default_projection.last_down_mask,
        .touch_lane_last_up_mask = default_projection.last_up_mask,
        .kps = source.kps,
        .source_sequence = event_cursor,
        .source_dropped_event_count = source.dropped_event_count,
        .consumer_dropped_event_count = consumer_dropped,
        .completed_raw_ns = realtime::monotonic_now_ns(),
        .session_anchor_raw_ns = source.session_anchor_raw_ns,
        .touch_lane_held_counts = default_projection.held_counts,
        .touch_lane_total_counts = default_projection.total_counts,
        .touch_lane_last_down_raw_ns = default_projection.last_down_raw_ns,
        .touch_lane_last_up_raw_ns = default_projection.last_up_raw_ns,
        .touch_lane_projections = projections,
    };
    g_snapshot_history[g_snapshot_write_index] = completed;
    g_snapshot_write_index = (g_snapshot_write_index + 1) % g_snapshot_history.size();
    g_snapshot_count = std::min(g_snapshot_count + 1, g_snapshot_history.size());
    return completed;
}

void worker_main() {
    uint64_t event_cursor = 0;
    uint64_t consumer_dropped = 0;
    uint32_t published_source_generation = std::numeric_limits<uint32_t>::max();
    uint64_t published_event_cursor = std::numeric_limits<uint64_t>::max();
    std::array<realtime::InputEvent, kEventBatchSize> event_batch{};
    CompletedInputSnapshot completed_snapshot{};

    for (;;) {
        for (;;) {
            const auto read = realtime::read_events(
                event_cursor,
                event_batch.data(),
                event_batch.size());
            consumer_dropped += read.dropped_before_cursor;
            event_cursor = read.cursor;
            if (read.count < event_batch.size())
                break;
        }

        auto source = realtime::read_input_snapshot();
        const int64_t now_ns = realtime::monotonic_now_ns();
        if (source.next_kps_expiry_ns > 0 && source.next_kps_expiry_ns <= now_ns) {
            realtime::refresh_kps(now_ns);
            source = realtime::read_input_snapshot();
        }

        if (source.generation != published_source_generation ||
            event_cursor != published_event_cursor) {
            realtime::InputCountJournalSnapshot count_journal{};
            realtime::read_input_checkpoint(source, count_journal);
            completed_snapshot = publish_snapshot(
                source,
                count_journal,
                event_cursor,
                consumer_dropped);
            published_source_generation = source.generation;
            published_event_cursor = event_cursor;
        }

        ClockAnchorSnapshot anchor{};
        read_latest_clock_anchor(anchor);
        if (completed_snapshot.available == 0)
            read_latest_input_snapshot(completed_snapshot);
        ui_recipe_runtime::process_triggers(completed_snapshot, anchor);
        run_presentation_scheduler(completed_snapshot, anchor);

        const auto next_presentation_wake = next_presentation_wake_raw_ns(
            completed_snapshot,
            anchor);
        const auto next_deferred_retry =
            ui_recipe_runtime::next_deferred_retry_raw_ns(completed_snapshot, anchor);
        int64_t wait_deadline = source.next_kps_expiry_ns;
        if (next_presentation_wake > 0 &&
            (wait_deadline <= 0 || next_presentation_wake < wait_deadline))
            wait_deadline = next_presentation_wake;
        if (next_deferred_retry > 0 &&
            (wait_deadline <= 0 || next_deferred_retry < wait_deadline))
            wait_deadline = next_deferred_retry;

        realtime::wait_for_change(
            event_cursor,
            source.generation,
            wait_deadline);
    }
}

}  // namespace

void ensure_started() {
    std::call_once(g_worker_once, [] {
        std::thread(worker_main).detach();
    });
}

bool read_latest_input_snapshot(CompletedInputSnapshot &snapshot) {
    std::unique_lock<std::mutex> lock(g_snapshot_lock, std::try_to_lock);
    if (!lock.owns_lock())
        return false;

    CompletedInputSnapshot best{};
    bool found = false;
    for (size_t index = 0; index < g_snapshot_count; ++index) {
        const auto &candidate = g_snapshot_history[index];
        if (candidate.available == 0)
            continue;
        if (!found || candidate.publication_generation > best.publication_generation) {
            best = candidate;
            found = true;
        }
    }
    if (found)
        snapshot = best;
    return found;
}

bool select_touch_lane_projection(const CompletedInputSnapshot &snapshot,
                                  uint32_t lane_count,
                                  TouchLaneProjectionSnapshot &projection) {
    for (const auto &candidate : snapshot.touch_lane_projections) {
        if (candidate.lane_count == lane_count) {
            projection = candidate;
            return true;
        }
    }
    return false;
}

void publish_clock_anchor(ClockAnchorSnapshot anchor) {
    if (anchor.monotonic_raw_ns <= 0)
        anchor.monotonic_raw_ns = realtime::monotonic_now_ns();

    {
        std::lock_guard<std::mutex> guard(g_clock_anchor_lock);
        anchor.available = 1;
        anchor.publication_generation = ++g_clock_anchor_publication_generation;
        g_clock_anchor_history[g_clock_anchor_write_index] = anchor;
        g_clock_anchor_write_index =
            (g_clock_anchor_write_index + 1) % g_clock_anchor_history.size();
        g_clock_anchor_count = std::min(
            g_clock_anchor_count + 1,
            g_clock_anchor_history.size());
    }
    if (has_pending_presentation_tasks() ||
        ui_recipe_runtime::needs_clock_anchor_wakeup())
        realtime::notify_waiters();
}

bool read_latest_clock_anchor(ClockAnchorSnapshot &anchor) {
    std::unique_lock<std::mutex> lock(g_clock_anchor_lock, std::try_to_lock);
    if (!lock.owns_lock())
        return false;

    ClockAnchorSnapshot best{};
    bool found = false;
    for (size_t index = 0; index < g_clock_anchor_count; ++index) {
        const auto &candidate = g_clock_anchor_history[index];
        if (candidate.available == 0)
            continue;
        if (!found || candidate.publication_generation > best.publication_generation) {
            best = candidate;
            found = true;
        }
    }
    if (found)
        anchor = best;
    return found;
}

}  // namespace starray::hud_logic

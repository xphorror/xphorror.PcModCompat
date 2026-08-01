#include "hud_deadline_scheduler.h"

#include "realtime_event_core.h"
#include "ui_recipe_lifecycle_runtime.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace starray::hud_logic {
namespace {

DeadlineScheduler g_scheduler;
std::mutex g_presentation_lock;
std::array<PresentationSnapshot, kPresentationSnapshotHistoryCapacity>
    g_presentation_history{};
size_t g_presentation_count = 0;
size_t g_presentation_write_index = 0;
uint32_t g_presentation_generation = 0;
std::atomic<uint32_t> g_presentation_generation_mirror{0};
std::atomic<uint32_t> g_presentation_acknowledged_generation{0};
std::atomic<uint64_t> g_presentation_history_overflow_count{0};
uint64_t g_presentation_clear_generation = 0;

bool valid_anchor(const ClockAnchorSnapshot &anchor, uint32_t bit) {
    return anchor.available != 0 && (anchor.valid_mask & bit) != 0 &&
           anchor.monotonic_raw_ns > 0;
}

int64_t seconds_to_ns(double seconds) {
    if (!std::isfinite(seconds))
        return 0;
    const auto scaled = seconds * 1'000'000'000.0;
    if (scaled >= static_cast<double>(std::numeric_limits<int64_t>::max()))
        return std::numeric_limits<int64_t>::max();
    if (scaled <= static_cast<double>(std::numeric_limits<int64_t>::min()))
        return std::numeric_limits<int64_t>::min();
    return static_cast<int64_t>(std::llround(scaled));
}

void publish_presentation_commands(
    const CompletedInputSnapshot &input,
    const std::array<PresentationCommand, kMaxDuePresentationTasks> &commands,
    size_t command_count,
    uint64_t dropped_stale,
    uint64_t clear_generation) {
    if (command_count == 0 && dropped_stale == 0)
        return;

    PresentationSnapshot snapshot{};
    snapshot.available = 1;
    snapshot.session_generation = input.session_generation;
    snapshot.command_count = static_cast<uint32_t>(command_count);
    snapshot.dropped_stale_tasks = dropped_stale;
    snapshot.scheduler_overflow_count = g_scheduler.dropped_overflow();
    snapshot.published_raw_ns = realtime::monotonic_now_ns();
    for (size_t index = 0; index < command_count; ++index)
        snapshot.commands[index] = commands[index];

    std::lock_guard<std::mutex> guard(g_presentation_lock);
    if (clear_generation != g_presentation_clear_generation)
        return;
    snapshot.publication_generation = ++g_presentation_generation;
    if (g_presentation_count == g_presentation_history.size()) {
        const auto &overwritten = g_presentation_history[g_presentation_write_index];
        if (overwritten.publication_generation != 0 &&
            overwritten.publication_generation >
                g_presentation_acknowledged_generation.load(std::memory_order_acquire)) {
            g_presentation_history_overflow_count.fetch_add(1, std::memory_order_relaxed);
        }
    }
    g_presentation_history[g_presentation_write_index] = snapshot;
    g_presentation_write_index =
        (g_presentation_write_index + 1) % g_presentation_history.size();
    g_presentation_count = std::min(g_presentation_count + 1, g_presentation_history.size());
    g_presentation_generation_mirror.store(
        g_presentation_generation,
        std::memory_order_release);
}

}  // namespace

bool DeadlineScheduler::less(const ScheduledPresentationTask &left,
                             const ScheduledPresentationTask &right) {
    if (left.deadline_ns != right.deadline_ns)
        return left.deadline_ns < right.deadline_ns;
    return left.sequence < right.sequence;
}

bool DeadlineScheduler::valid_domain(ClockDomain domain) {
    const auto value = static_cast<size_t>(domain);
    return value < kClockDomainCount;
}

size_t DeadlineScheduler::queue_index(ClockDomain domain, uint32_t flags) {
    const auto extrapolated = (flags & SchedulerAllowAnchorExtrapolation) != 0;
    return static_cast<size_t>(domain) + (extrapolated ? kClockDomainCount : 0);
}

bool DeadlineScheduler::read_domain_now_ns(const SchedulerClock &clock,
                                           ClockDomain domain,
                                           bool allow_extrapolation,
                                           int64_t &value_ns) {
    double rate = 0.0;
    switch (domain) {
    case ClockDomain::Realtime:
        value_ns = clock.realtime_now_ns;
        return value_ns > 0;
    case ClockDomain::UnityScaled:
        if (!valid_anchor(clock.anchor, ClockAnchorUnityScaled))
            return false;
        value_ns = seconds_to_ns(clock.anchor.unity_scaled_seconds);
        rate = std::isfinite(clock.anchor.unity_time_scale)
            ? std::max(0.0f, clock.anchor.unity_time_scale)
            : 0.0;
        break;
    case ClockDomain::Song:
        if (!valid_anchor(clock.anchor, ClockAnchorSongPosition))
            return false;
        value_ns = seconds_to_ns(clock.anchor.song_position_seconds);
        rate = 1.0;
        break;
    case ClockDomain::Audio:
        if (!valid_anchor(clock.anchor, ClockAnchorAudioPosition))
            return false;
        value_ns = seconds_to_ns(clock.anchor.audio_position_seconds);
        rate = 1.0;
        break;
    case ClockDomain::Map:
        if (!valid_anchor(clock.anchor, ClockAnchorMapPosition))
            return false;
        value_ns = seconds_to_ns(clock.anchor.map_position_seconds);
        rate = 1.0;
        break;
    }

    if (!allow_extrapolation || rate <= 0.0 ||
        clock.realtime_now_ns <= clock.anchor.monotonic_raw_ns)
        return true;

    const auto extrapolated = static_cast<long double>(value_ns) +
        static_cast<long double>(clock.realtime_now_ns - clock.anchor.monotonic_raw_ns) * rate;
    if (extrapolated >= static_cast<long double>(std::numeric_limits<int64_t>::max()))
        value_ns = std::numeric_limits<int64_t>::max();
    else if (extrapolated <= static_cast<long double>(std::numeric_limits<int64_t>::min()))
        value_ns = std::numeric_limits<int64_t>::min();
    else
        value_ns = static_cast<int64_t>(extrapolated);
    return true;
}

int64_t DeadlineScheduler::estimate_raw_deadline_ns(
    const SchedulerClock &clock,
    const ScheduledPresentationTask &task) {
    if (task.domain == ClockDomain::Realtime)
        return task.deadline_ns;

    const bool allow_extrapolation =
        (task.flags & SchedulerAllowAnchorExtrapolation) != 0;
    int64_t current_domain_ns = 0;
    if (!read_domain_now_ns(clock, task.domain, allow_extrapolation, current_domain_ns) ||
        clock.anchor.monotonic_raw_ns <= 0)
        return std::numeric_limits<int64_t>::max();

    if (task.deadline_ns <= current_domain_ns)
        return clock.realtime_now_ns;

    if (!allow_extrapolation)
        return std::numeric_limits<int64_t>::max();

    double rate = 1.0;
    if (task.domain == ClockDomain::UnityScaled) {
        if (!std::isfinite(clock.anchor.unity_time_scale) ||
            clock.anchor.unity_time_scale <= 0.0f)
            return std::numeric_limits<int64_t>::max();
        rate = clock.anchor.unity_time_scale;
    }

    const auto delta = static_cast<double>(task.deadline_ns - current_domain_ns) / rate;
    if (delta >= static_cast<double>(std::numeric_limits<int64_t>::max() - clock.realtime_now_ns))
        return std::numeric_limits<int64_t>::max();
    return clock.realtime_now_ns + static_cast<int64_t>(std::max(0.0, delta));
}

void DeadlineScheduler::push(DomainHeap &heap, ScheduledPresentationTask task) {
    size_t index = heap.size++;
    heap.values[index] = task;
    while (index > 0) {
        const size_t parent = (index - 1) / 2;
        if (!less(heap.values[index], heap.values[parent]))
            break;
        std::swap(heap.values[index], heap.values[parent]);
        index = parent;
    }
}

ScheduledPresentationTask DeadlineScheduler::pop(DomainHeap &heap) {
    const auto result = heap.values[0];
    --heap.size;
    if (heap.size == 0)
        return result;

    heap.values[0] = heap.values[heap.size];
    size_t index = 0;
    for (;;) {
        const size_t left = index * 2 + 1;
        const size_t right = left + 1;
        size_t smallest = index;
        if (left < heap.size && less(heap.values[left], heap.values[smallest]))
            smallest = left;
        if (right < heap.size && less(heap.values[right], heap.values[smallest]))
            smallest = right;
        if (smallest == index)
            break;
        std::swap(heap.values[index], heap.values[smallest]);
        index = smallest;
    }
    return result;
}

void DeadlineScheduler::discard_stale_locked(uint32_t active_session_generation,
                                             uint64_t &dropped_stale) {
    if (active_session_generation == 0)
        return;

    for (auto &heap : heaps_) {
        std::array<ScheduledPresentationTask, kMaxScheduledPresentationTasksPerDomain> retained{};
        size_t retained_count = 0;
        while (heap.size != 0) {
            auto task = pop(heap);
            if (task.session_generation != 0 &&
                task.session_generation != active_session_generation) {
                ++dropped_stale;
                continue;
            }
            retained[retained_count++] = task;
        }
        for (size_t index = 0; index < retained_count; ++index)
            push(heap, retained[index]);
    }
}

bool DeadlineScheduler::schedule(ScheduledPresentationTask task) {
    if (!valid_domain(task.domain) ||
        (task.flags & ~(SchedulerAllowAnchorExtrapolation | SchedulerInternalVmExecution)) != 0)
        return false;
    const auto index = queue_index(task.domain, task.flags);
    std::lock_guard<std::mutex> guard(lock_);
    auto &heap = heaps_[index];
    if (heap.size >= heap.values.size()) {
        ++dropped_overflow_;
        return false;
    }
    task.sequence = next_sequence_++;
    push(heap, task);
    pending_task_count_.fetch_add(1, std::memory_order_release);
    return true;
}

size_t DeadlineScheduler::pop_due(
    const SchedulerClock &clock,
    uint32_t active_session_generation,
    std::array<ScheduledPresentationTask, kMaxDuePresentationTasks> &output,
    uint64_t &dropped_stale) {
    std::lock_guard<std::mutex> guard(lock_);
    dropped_stale = 0;
    discard_stale_locked(active_session_generation, dropped_stale);
    if (dropped_stale != 0)
        pending_task_count_.fetch_sub(static_cast<size_t>(dropped_stale), std::memory_order_acq_rel);

    size_t count = 0;
    for (size_t queue = 0; queue < heaps_.size() && count < output.size(); ++queue) {
        auto &heap = heaps_[queue];
        while (heap.size != 0 && count < output.size()) {
            const auto &next = heap.values[0];
            int64_t now_domain_ns = 0;
            const bool allow_extrapolation =
                (next.flags & SchedulerAllowAnchorExtrapolation) != 0;
            if (!read_domain_now_ns(clock, next.domain, allow_extrapolation, now_domain_ns) ||
                next.deadline_ns > now_domain_ns)
                break;
            output[count++] = pop(heap);
        }
    }

    std::sort(output.begin(), output.begin() + static_cast<std::ptrdiff_t>(count),
              [](const auto &left, const auto &right) { return left.sequence < right.sequence; });
    if (count != 0)
        pending_task_count_.fetch_sub(count, std::memory_order_acq_rel);
    return count;
}

int64_t DeadlineScheduler::next_wake_raw_ns(const SchedulerClock &clock,
                                            uint32_t active_session_generation) {
    std::lock_guard<std::mutex> guard(lock_);
    uint64_t discarded = 0;
    discard_stale_locked(active_session_generation, discarded);
    if (discarded != 0)
        pending_task_count_.fetch_sub(static_cast<size_t>(discarded), std::memory_order_acq_rel);
    auto result = std::numeric_limits<int64_t>::max();
    for (const auto &heap : heaps_) {
        if (heap.size == 0)
            continue;
        result = std::min(
            result,
            estimate_raw_deadline_ns(clock, heap.values[0]));
    }
    return result == std::numeric_limits<int64_t>::max() ? 0 : result;
}

size_t DeadlineScheduler::size() const {
    return pending_task_count_.load(std::memory_order_acquire);
}

bool DeadlineScheduler::has_pending_tasks() const {
    return pending_task_count_.load(std::memory_order_acquire) != 0;
}

uint64_t DeadlineScheduler::dropped_overflow() const {
    std::lock_guard<std::mutex> guard(lock_);
    return dropped_overflow_;
}

size_t DeadlineScheduler::cancel_generation(uint32_t generation) {
    if (generation == 0)
        return 0;

    std::lock_guard<std::mutex> guard(lock_);
    size_t removed = 0;
    for (auto &heap : heaps_) {
        std::array<ScheduledPresentationTask, kMaxScheduledPresentationTasksPerDomain> retained{};
        size_t retained_count = 0;
        while (heap.size != 0) {
            auto task = pop(heap);
            if (task.generation == generation) {
                ++removed;
                continue;
            }
            retained[retained_count++] = task;
        }
        for (size_t index = 0; index < retained_count; ++index)
            push(heap, retained[index]);
    }
    if (removed != 0)
        pending_task_count_.fetch_sub(removed, std::memory_order_acq_rel);
    return removed;
}

void DeadlineScheduler::reset() {
    std::lock_guard<std::mutex> guard(lock_);
    for (auto &heap : heaps_)
        heap.size = 0;
    next_sequence_ = 1;
    dropped_overflow_ = 0;
    pending_task_count_.store(0, std::memory_order_release);
}

bool schedule_presentation_task(const ScheduledPresentationTask &task) {
    ensure_started();
    if (!g_scheduler.schedule(task))
        return false;
    realtime::notify_waiters();
    return true;
}

bool has_pending_presentation_tasks() {
    return g_scheduler.has_pending_tasks();
}

size_t cancel_presentation_tasks(uint32_t generation) {
    const auto removed = g_scheduler.cancel_generation(generation);
    if (removed != 0)
        realtime::notify_waiters();
    return removed;
}

void clear_presentation_tasks() {
    g_scheduler.reset();
    std::lock_guard<std::mutex> guard(g_presentation_lock);
    ++g_presentation_clear_generation;
    g_presentation_history = {};
    PresentationSnapshot barrier{};
    barrier.publication_generation = ++g_presentation_generation;
    barrier.published_raw_ns = realtime::monotonic_now_ns();
    g_presentation_history[0] = barrier;
    g_presentation_count = 1;
    g_presentation_write_index = 1;
    g_presentation_generation_mirror.store(
        g_presentation_generation,
        std::memory_order_release);
}

uint32_t presentation_publication_generation() {
    return g_presentation_generation_mirror.load(std::memory_order_acquire);
}

uint64_t presentation_history_overflow_count() {
    return g_presentation_history_overflow_count.load(std::memory_order_acquire);
}

void acknowledge_presentation_generation(uint32_t generation) {
    auto current = g_presentation_acknowledged_generation.load(std::memory_order_acquire);
    while (generation > current &&
           !g_presentation_acknowledged_generation.compare_exchange_weak(
               current,
               generation,
               std::memory_order_release,
               std::memory_order_acquire)) {
    }
}

bool read_latest_presentation_snapshot(PresentationSnapshot &snapshot) {
    std::unique_lock<std::mutex> lock(g_presentation_lock, std::try_to_lock);
    if (!lock.owns_lock())
        return false;

    PresentationSnapshot best{};
    bool found = false;
    for (size_t index = 0; index < g_presentation_count; ++index) {
        const auto &candidate = g_presentation_history[index];
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

bool read_presentation_snapshot(uint32_t requested_generation,
                                PresentationSnapshot &snapshot) {
    std::unique_lock<std::mutex> lock(g_presentation_lock, std::try_to_lock);
    if (!lock.owns_lock())
        return false;

    if (requested_generation == g_presentation_generation)
        return false;

    PresentationSnapshot best{};
    bool found = false;
    for (size_t index = 0; index < g_presentation_count; ++index) {
        const auto &candidate = g_presentation_history[index];
        if (candidate.available == 0)
            continue;
        if (!found || candidate.publication_generation > best.publication_generation) {
            best = candidate;
            found = true;
        }
    }
    if (found) {
        snapshot = best;
        return true;
    }

    snapshot = {};
    snapshot.publication_generation = g_presentation_generation;
    return true;
}

bool read_next_presentation_snapshot(uint32_t requested_generation,
                                     PresentationSnapshot &snapshot) {
    std::unique_lock<std::mutex> lock(g_presentation_lock, std::try_to_lock);
    if (!lock.owns_lock())
        return false;
    if (requested_generation == g_presentation_generation)
        return false;

    PresentationSnapshot next{};
    bool found = false;
    for (size_t index = 0; index < g_presentation_count; ++index) {
        const auto &candidate = g_presentation_history[index];
        if (candidate.publication_generation == 0 ||
            candidate.publication_generation <= requested_generation) {
            continue;
        }
        if (!found || candidate.publication_generation < next.publication_generation) {
            next = candidate;
            found = true;
        }
    }
    if (found) {
        next.history_gap = requested_generation != 0 &&
            next.publication_generation != requested_generation + 1u;
        snapshot = next;
        return true;
    }

    // clear_presentation_tasks() advances the generation while emptying the
    // history.  Publish that barrier so the consumer cannot retain a stale
    // cursor or replay pre-clear commands after a later bundle load.
    snapshot = {};
    snapshot.publication_generation = g_presentation_generation;
    snapshot.history_gap = requested_generation != 0 &&
        snapshot.publication_generation != requested_generation + 1u;
    return true;
}

void reset_presentation_scheduler_for_tests() {
    g_scheduler.reset();
    std::lock_guard<std::mutex> guard(g_presentation_lock);
    ++g_presentation_clear_generation;
    g_presentation_history = {};
    g_presentation_count = 0;
    g_presentation_write_index = 0;
    g_presentation_generation = 0;
    g_presentation_generation_mirror.store(0, std::memory_order_release);
    g_presentation_acknowledged_generation.store(0, std::memory_order_release);
    g_presentation_history_overflow_count.store(0, std::memory_order_release);
}

// Called by HudLogicWorker after input and clock snapshots have been caught up.
void run_presentation_scheduler(const CompletedInputSnapshot &input,
                                const ClockAnchorSnapshot &anchor) {
    if (!g_scheduler.has_pending_tasks())
        return;
    std::array<ScheduledPresentationTask, kMaxDuePresentationTasks> due{};
    uint64_t dropped_stale = 0;
    const auto count = g_scheduler.pop_due(
        SchedulerClock{realtime::monotonic_now_ns(), anchor},
        input.session_generation,
        due,
        dropped_stale);
    uint64_t clear_generation = 0;
    {
        std::lock_guard<std::mutex> guard(g_presentation_lock);
        clear_generation = g_presentation_clear_generation;
    }
    std::array<PresentationCommand, kMaxDuePresentationTasks> commands{};
    size_t command_count = 0;
    for (size_t index = 0; index < count; ++index) {
        const auto &task = due[index];
        if ((task.flags & SchedulerInternalVmExecution) != 0) {
            if (command_count >= commands.size())
                break;
            if (ui_recipe_runtime::execute_scheduled_task(
                    task,
                    input,
                    anchor,
                    commands[command_count]) == ui_recipe_runtime::ExecutionOutcome::Produced)
                ++command_count;
            continue;
        }
        commands[command_count++] = PresentationCommand{
            .sequence = task.sequence,
            .session_generation = task.session_generation,
            .generation = task.generation,
            .rule_id = task.rule_id,
            .command_type = task.command_type,
            .target_id = task.target_id,
            .payload0 = task.payload0,
            .payload1 = task.payload1,
            .value0 = task.value0,
            .value1 = task.value1,
        };
    }
    publish_presentation_commands(
        input,
        commands,
        command_count,
        dropped_stale,
        clear_generation);
}

int64_t next_presentation_wake_raw_ns(const CompletedInputSnapshot &input,
                                      const ClockAnchorSnapshot &anchor) {
    if (!g_scheduler.has_pending_tasks())
        return 0;
    return g_scheduler.next_wake_raw_ns(
        SchedulerClock{realtime::monotonic_now_ns(), anchor},
        input.session_generation);
}

}  // namespace starray::hud_logic

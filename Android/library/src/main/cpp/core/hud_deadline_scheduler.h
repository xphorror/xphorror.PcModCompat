#pragma once

#include "hud_logic_worker.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>

namespace starray::hud_logic {

enum class ClockDomain : uint8_t {
    Realtime = 0,
    UnityScaled = 1,
    Song = 2,
    Audio = 3,
    Map = 4,
};

constexpr size_t kClockDomainCount = 5;
constexpr size_t kSchedulerQueueCount = kClockDomainCount * 2;
constexpr size_t kMaxScheduledPresentationTasksPerDomain = 64;
constexpr size_t kMaxDuePresentationTasks = 64;
constexpr size_t kPresentationSnapshotHistoryCapacity = 64;

enum SchedulerTaskFlags : uint32_t {
    SchedulerAllowAnchorExtrapolation = 1u << 0,
    SchedulerInternalVmExecution = 1u << 1,
};

struct ScheduledPresentationTask {
    uint64_t sequence = 0;
    uint32_t session_generation = 0;
    uint32_t generation = 0;
    uint32_t rule_id = 0;
    uint32_t program_index = 0;
    uint32_t target_id = 0;
    ClockDomain domain = ClockDomain::Realtime;
    int64_t deadline_ns = 0;
    uint32_t command_type = 0;
    uint32_t flags = 0;
    int64_t payload0 = 0;
    int64_t payload1 = 0;
    float value0 = 0.0f;
    float value1 = 0.0f;
};

struct SchedulerClock {
    int64_t realtime_now_ns = 0;
    ClockAnchorSnapshot anchor{};
};

class DeadlineScheduler final {
public:
    bool schedule(ScheduledPresentationTask task);

    size_t pop_due(const SchedulerClock &clock,
                   uint32_t active_session_generation,
                   std::array<ScheduledPresentationTask, kMaxDuePresentationTasks> &output,
                   uint64_t &dropped_stale);

    int64_t next_wake_raw_ns(const SchedulerClock &clock,
                             uint32_t active_session_generation);

    size_t size() const;
    bool has_pending_tasks() const;
    uint64_t dropped_overflow() const;
    size_t cancel_generation(uint32_t generation);
    void reset();

private:
    struct DomainHeap {
        std::array<ScheduledPresentationTask, kMaxScheduledPresentationTasksPerDomain> values{};
        size_t size = 0;
    };

    static bool less(const ScheduledPresentationTask &left,
                     const ScheduledPresentationTask &right);
    static bool valid_domain(ClockDomain domain);
    static size_t queue_index(ClockDomain domain, uint32_t flags);
    static bool read_domain_now_ns(const SchedulerClock &clock,
                                   ClockDomain domain,
                                   bool allow_extrapolation,
                                   int64_t &value_ns);
    static int64_t estimate_raw_deadline_ns(const SchedulerClock &clock,
                                            const ScheduledPresentationTask &task);

    void discard_stale_locked(uint32_t active_session_generation,
                              uint64_t &dropped_stale);
    static void push(DomainHeap &heap, ScheduledPresentationTask task);
    static ScheduledPresentationTask pop(DomainHeap &heap);

    mutable std::mutex lock_;
    std::array<DomainHeap, kSchedulerQueueCount> heaps_{};
    uint64_t next_sequence_ = 1;
    uint64_t dropped_overflow_ = 0;
    std::atomic<size_t> pending_task_count_{0};
};

struct PresentationCommand {
    uint64_t sequence = 0;
    uint32_t session_generation = 0;
    uint32_t generation = 0;
    uint32_t rule_id = 0;
    uint32_t command_type = 0;
    uint32_t target_id = 0;
    int64_t payload0 = 0;
    int64_t payload1 = 0;
    float value0 = 0.0f;
    float value1 = 0.0f;
};

struct PresentationSnapshot {
    uint32_t available = 0;
    uint32_t publication_generation = 0;
    uint32_t session_generation = 0;
    uint32_t command_count = 0;
    uint32_t history_gap = 0;
    uint64_t dropped_stale_tasks = 0;
    uint64_t scheduler_overflow_count = 0;
    int64_t published_raw_ns = 0;
    std::array<PresentationCommand, kMaxDuePresentationTasks> commands{};
};

bool schedule_presentation_task(const ScheduledPresentationTask &task);
bool has_pending_presentation_tasks();
uint32_t presentation_publication_generation();
uint64_t presentation_history_overflow_count();
void acknowledge_presentation_generation(uint32_t generation);
bool read_latest_presentation_snapshot(PresentationSnapshot &snapshot);
bool read_presentation_snapshot(uint32_t requested_generation,
                                PresentationSnapshot &snapshot);
bool read_next_presentation_snapshot(uint32_t requested_generation,
                                     PresentationSnapshot &snapshot);
size_t cancel_presentation_tasks(uint32_t generation);
void clear_presentation_tasks();
void reset_presentation_scheduler_for_tests();

void run_presentation_scheduler(const CompletedInputSnapshot &input,
                                const ClockAnchorSnapshot &anchor);

int64_t next_presentation_wake_raw_ns(const CompletedInputSnapshot &input,
                                      const ClockAnchorSnapshot &anchor);

}  // namespace starray::hud_logic

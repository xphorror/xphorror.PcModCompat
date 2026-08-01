#include "ui_recipe_lifecycle_runtime.h"

#include "realtime_event_core.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <iterator>
#include <limits>
#include <mutex>

namespace starray::ui_recipe_runtime {
namespace {

constexpr uint32_t kMaxInstructionBudget = 1'000'000;

struct ProgramState {
    uint32_t bundle_id = 0;
    pccompat_recipe::LifecycleProgram descriptor{};
    std::vector<rule_vm::Instruction> instructions;
    rule_vm::RuleRuntime runtime{};
    std::atomic<uint32_t> active{1};
    std::atomic<uint32_t> presentation_enabled{1};
    std::atomic<uint32_t> queued{0};
    bool bundle_load_attempted = false;
    bool deferred = false;
    uint32_t last_session_generation = std::numeric_limits<uint32_t>::max();
    uint32_t last_input_generation = 0;
    uint32_t last_clock_generation = 0;
    uint32_t last_overlay_generation = 0;
    uint32_t deferred_input_generation = 0;
    uint32_t deferred_clock_generation = 0;
    uint32_t deferred_overlay_generation = 0;
    int64_t deferred_retry_raw_ns = 0;
};

struct OverlayStateSnapshot {
    uint32_t available = 0;
    uint32_t generation = 0;
    uint32_t visible = 0;
};

std::mutex g_lifecycle_lock;
std::mutex g_registry_lock;
std::condition_variable g_inflight_condition;
std::deque<ProgramState> g_programs;
uint32_t g_inflight_executions = 0;
std::atomic<uint32_t> g_clock_wakeup_interest{0};
std::atomic<uint32_t> g_worker_rescan_requested{0};
std::atomic<uint32_t> g_overlay_available{0};
std::atomic<uint32_t> g_overlay_generation{0};
std::atomic<uint32_t> g_overlay_visible{0};

OverlayStateSnapshot read_overlay_state() {
    OverlayStateSnapshot snapshot{};
    snapshot.available = g_overlay_available.load(std::memory_order_acquire);
    if (snapshot.available == 0)
        return snapshot;
    snapshot.generation = g_overlay_generation.load(std::memory_order_acquire);
    snapshot.visible = g_overlay_visible.load(std::memory_order_acquire);
    return snapshot;
}

class ExecutionLease final {
public:
    ExecutionLease() = default;
    ExecutionLease(const ExecutionLease &) = delete;
    ExecutionLease &operator=(const ExecutionLease &) = delete;

    ~ExecutionLease() {
        if (!active_)
            return;
        std::lock_guard<std::mutex> guard(g_registry_lock);
        --g_inflight_executions;
        g_inflight_condition.notify_all();
    }

    void acquire() {
        ++g_inflight_executions;
        active_ = true;
    }

private:
    bool active_ = false;
};

bool clock_domain_now_ns(
    const hud_logic::ClockAnchorSnapshot &anchor,
    pccompat_recipe::LifecycleClockDomain domain,
    int64_t realtime_now_ns,
    int64_t &value_ns) {
    if (domain == pccompat_recipe::LifecycleClockDomain::Realtime) {
        value_ns = realtime_now_ns;
        return value_ns > 0;
    }
    if (anchor.available == 0 || anchor.monotonic_raw_ns <= 0)
        return false;

    double seconds = 0.0;
    uint32_t valid_bit = 0;
    switch (domain) {
    case pccompat_recipe::LifecycleClockDomain::UnityScaled:
        seconds = anchor.unity_scaled_seconds;
        valid_bit = hud_logic::ClockAnchorUnityScaled;
        break;
    case pccompat_recipe::LifecycleClockDomain::Song:
        seconds = anchor.song_position_seconds;
        valid_bit = hud_logic::ClockAnchorSongPosition;
        break;
    case pccompat_recipe::LifecycleClockDomain::Audio:
        seconds = anchor.audio_position_seconds;
        valid_bit = hud_logic::ClockAnchorAudioPosition;
        break;
    case pccompat_recipe::LifecycleClockDomain::Map:
        seconds = anchor.map_position_seconds;
        valid_bit = hud_logic::ClockAnchorMapPosition;
        break;
    case pccompat_recipe::LifecycleClockDomain::Realtime:
        break;
    }
    if ((anchor.valid_mask & valid_bit) == 0 || !std::isfinite(seconds))
        return false;
    const auto scaled = seconds * 1'000'000'000.0;
    if (scaled >= static_cast<double>(std::numeric_limits<int64_t>::max()) ||
        scaled <= static_cast<double>(std::numeric_limits<int64_t>::min()))
        return false;
    value_ns = static_cast<int64_t>(std::llround(scaled));
    return true;
}

bool dependency_available(
    const pccompat_recipe::LifecycleProgram &program,
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor) {
    if ((program.flags & pccompat_recipe::LifecycleRequireInputSnapshot) != 0 &&
        input.available == 0)
        return false;
    if ((program.flags & pccompat_recipe::LifecycleRequireClockAnchor) != 0 &&
        anchor.available == 0)
        return false;
    return true;
}

bool should_retry_deferred(
    const ProgramState &state,
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor,
    const OverlayStateSnapshot &overlay,
    int64_t now_ns) {
    if (!state.deferred)
        return true;
    const bool dependency_changed =
        state.deferred_input_generation != input.publication_generation ||
        state.deferred_clock_generation != anchor.publication_generation ||
        state.deferred_overlay_generation != overlay.generation;
    return dependency_changed && now_ns >= state.deferred_retry_raw_ns;
}

int64_t saturating_add_ns(int64_t value, int64_t delta) {
    if (delta > 0 && value > std::numeric_limits<int64_t>::max() - delta)
        return std::numeric_limits<int64_t>::max();
    return value + delta;
}

bool task_deadline(
    const pccompat_recipe::LifecycleProgram &program,
    const hud_logic::ClockAnchorSnapshot &anchor,
    int64_t now_ns,
    int64_t &deadline_ns) {
    int64_t current = 0;
    if (!clock_domain_now_ns(anchor, program.clock_domain, now_ns, current))
        return false;
    if (program.initial_delay_ns >
        std::numeric_limits<int64_t>::max() - current)
        return false;
    deadline_ns = current + program.initial_delay_ns;
    return true;
}

uint32_t scheduler_flags(const pccompat_recipe::LifecycleProgram &program) {
    uint32_t flags = hud_logic::SchedulerInternalVmExecution;
    if ((program.flags & pccompat_recipe::LifecycleAllowAnchorExtrapolation) != 0)
        flags |= hud_logic::SchedulerAllowAnchorExtrapolation;
    return flags;
}

bool schedule_program_locked(
    size_t index,
    ProgramState &state,
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor) {
    const auto overlay = read_overlay_state();
    if (state.active.load(std::memory_order_acquire) == 0 ||
        state.queued.exchange(1, std::memory_order_acq_rel) != 0)
        return false;

    int64_t deadline = 0;
    if (!task_deadline(
            state.descriptor,
            anchor,
            realtime::monotonic_now_ns(),
            deadline)) {
        state.queued.store(0, std::memory_order_release);
        return false;
    }

    hud_logic::ScheduledPresentationTask task{};
    task.session_generation = input.session_generation;
    task.generation = state.bundle_id;
    task.rule_id = state.descriptor.runtime_rule_id;
    task.program_index = static_cast<uint32_t>(index);
    task.target_id = state.descriptor.target_id;
    task.domain = static_cast<hud_logic::ClockDomain>(state.descriptor.clock_domain);
    task.deadline_ns = deadline;
    task.command_type = state.descriptor.command_type;
    task.flags = scheduler_flags(state.descriptor);
    if (!hud_logic::schedule_presentation_task(task)) {
        state.queued.store(0, std::memory_order_release);
        return false;
    }
    return true;
}

void initialize_program_state(
    ProgramState &state,
    uint32_t bundle_id,
    const pccompat_recipe::LifecycleProgram &descriptor,
    const std::vector<rule_vm::Instruction> &bytecode) {
    state.bundle_id = bundle_id;
    state.descriptor = descriptor;
    state.instructions.assign(
        bytecode.begin() + descriptor.program_start,
        bytecode.begin() + descriptor.program_start + descriptor.program_count);
    state.runtime.rule_id = descriptor.runtime_rule_id;
    state.runtime.instruction_budget = descriptor.instruction_budget;
    rule_vm::reset_rule_runtime(state.runtime);
    state.bundle_load_attempted = false;
    state.deferred = false;
    state.last_session_generation = std::numeric_limits<uint32_t>::max();
    state.last_input_generation = 0;
    state.last_clock_generation = 0;
    state.last_overlay_generation = 0;
    state.deferred_input_generation = 0;
    state.deferred_clock_generation = 0;
    state.deferred_overlay_generation = 0;
    state.deferred_retry_raw_ns = 0;
    state.queued.store(0, std::memory_order_release);
    state.presentation_enabled.store(1, std::memory_order_release);
    state.active.store(1, std::memory_order_release);
}

void refresh_clock_wakeup_interest_locked() {
    const bool needed = std::any_of(
        g_programs.begin(),
        g_programs.end(),
        [](const ProgramState &state) {
            return state.bundle_id != 0 &&
                state.active.load(std::memory_order_acquire) != 0 &&
                (state.descriptor.trigger == pccompat_recipe::LifecycleTrigger::ClockAnchorChanged ||
                 (state.descriptor.flags & pccompat_recipe::LifecycleRequireClockAnchor) != 0);
        });
    g_clock_wakeup_interest.store(needed ? 1u : 0u, std::memory_order_release);
}

}  // namespace

bool register_bundle(
    uint32_t bundle_id,
    const std::vector<rule_vm::Instruction> &bytecode,
    const std::vector<pccompat_recipe::LifecycleProgram> &programs,
    std::string &error) {
    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_lock);
    if (programs.empty())
        return true;
    if (programs.size() > kMaxPrograms ||
        bytecode.empty() || bytecode.size() > rule_vm::kMaxProgramInstructions * kMaxPrograms) {
        error = "lifecycle registry capacity exceeded";
        return false;
    }

    {
        std::lock_guard<std::mutex> guard(g_registry_lock);
        const size_t reusable = static_cast<size_t>(std::count_if(
            g_programs.begin(),
            g_programs.end(),
            [](const ProgramState &state) { return state.bundle_id == 0; }));
        const size_t append_count = programs.size() > reusable
            ? programs.size() - reusable
            : 0;
        if (g_programs.size() + append_count > kMaxPrograms) {
            error = "lifecycle registry program capacity exceeded";
            return false;
        }

        // Validate every descriptor before mutating the append-only registry.
        // A malformed later record must not leave an earlier record active.
        for (const auto &descriptor : programs) {
            if (descriptor.trigger < pccompat_recipe::LifecycleTrigger::BundleLoad ||
                descriptor.trigger > pccompat_recipe::LifecycleTrigger::OverlayStateChanged ||
                descriptor.clock_domain > pccompat_recipe::LifecycleClockDomain::Map ||
                descriptor.program_start > bytecode.size() ||
                descriptor.program_count > bytecode.size() - descriptor.program_start ||
                descriptor.program_count == 0 ||
                descriptor.instruction_budget == 0 ||
                descriptor.instruction_budget > kMaxInstructionBudget ||
                descriptor.command_type == 0 ||
                descriptor.initial_delay_ns < 0 ||
                descriptor.deferred_retry_delay_ns <= 0) {
                error = "lifecycle descriptor is invalid";
                return false;
            }
        }

        for (const auto &descriptor : programs) {
            auto reusable_state = std::find_if(
                g_programs.begin(),
                g_programs.end(),
                [](const ProgramState &state) { return state.bundle_id == 0; });
            if (reusable_state == g_programs.end()) {
                g_programs.emplace_back();
                reusable_state = std::prev(g_programs.end());
            }
            initialize_program_state(*reusable_state, bundle_id, descriptor, bytecode);
        }
    }

    const bool needs_clock_wakeup = std::any_of(
        programs.begin(),
        programs.end(),
        [](const pccompat_recipe::LifecycleProgram &program) {
            return program.trigger == pccompat_recipe::LifecycleTrigger::ClockAnchorChanged ||
                (program.flags & pccompat_recipe::LifecycleRequireClockAnchor) != 0;
        });
    if (needs_clock_wakeup)
        g_clock_wakeup_interest.store(1, std::memory_order_release);
    hud_logic::ensure_started();
    realtime::notify_waiters();
    return true;
}

bool set_bundle_presentation_enabled(uint32_t bundle_id, bool enabled) {
    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_lock);
    bool found = false;
    {
        std::lock_guard<std::mutex> guard(g_registry_lock);
        for (auto &state : g_programs) {
            if (state.bundle_id != bundle_id)
                continue;
            found = true;
            state.presentation_enabled.store(enabled ? 1u : 0u, std::memory_order_release);
            if (!enabled) {
                state.deferred = false;
                state.deferred_retry_raw_ns = 0;
                continue;
            }
            state.bundle_load_attempted = false;
            state.last_session_generation = std::numeric_limits<uint32_t>::max();
            state.last_input_generation = 0;
            state.last_clock_generation = 0;
            state.last_overlay_generation = 0;
            rule_vm::reset_rule_runtime(state.runtime);
        }
    }
    if (found) {
        g_worker_rescan_requested.store(1u, std::memory_order_release);
        realtime::notify_waiters();
    }
    return found;
}

bool retire_bundle(uint32_t bundle_id) {
    if (bundle_id == 0)
        return false;

    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_lock);
    bool found = false;
    {
        std::lock_guard<std::mutex> guard(g_registry_lock);
        for (auto &state : g_programs) {
            if (state.bundle_id != bundle_id)
                continue;
            found = true;
            state.active.store(0, std::memory_order_release);
            state.presentation_enabled.store(0, std::memory_order_release);
            state.queued.store(0, std::memory_order_release);
            state.deferred = false;
            state.deferred_retry_raw_ns = 0;
        }
    }
    if (!found)
        return false;

    hud_logic::cancel_presentation_tasks(bundle_id);
    {
        std::unique_lock<std::mutex> lock(g_registry_lock);
        g_inflight_condition.wait(lock, [] { return g_inflight_executions == 0; });
        for (auto &state : g_programs) {
            if (state.bundle_id != bundle_id)
                continue;
            state.instructions.clear();
            state.descriptor = {};
            state.runtime.rule_id = 0;
            state.runtime.instruction_budget = rule_vm::kDefaultInstructionBudget;
            rule_vm::reset_rule_runtime(state.runtime);
            state.bundle_load_attempted = false;
            state.last_session_generation = std::numeric_limits<uint32_t>::max();
            state.last_input_generation = 0;
            state.last_clock_generation = 0;
            state.last_overlay_generation = 0;
            state.deferred_input_generation = 0;
            state.deferred_clock_generation = 0;
            state.deferred_overlay_generation = 0;
            state.bundle_id = 0;
        }
        refresh_clock_wakeup_interest_locked();
    }
    g_worker_rescan_requested.store(1u, std::memory_order_release);
    realtime::notify_waiters();
    return true;
}

void clear() {
    std::lock_guard<std::mutex> lifecycle_guard(g_lifecycle_lock);
    {
        std::lock_guard<std::mutex> guard(g_registry_lock);
        for (auto &state : g_programs) {
            state.active.store(0, std::memory_order_release);
            state.queued.store(0, std::memory_order_release);
            state.deferred = false;
            state.deferred_retry_raw_ns = 0;
        }
    }
    g_clock_wakeup_interest.store(0, std::memory_order_release);
    g_worker_rescan_requested.store(0, std::memory_order_release);
    // Do this outside the registry lock.  A due task may be in the scheduler
    // while another thread is clearing the registry.
    hud_logic::clear_presentation_tasks();
    {
        std::unique_lock<std::mutex> lock(g_registry_lock);
        g_inflight_condition.wait(lock, [] { return g_inflight_executions == 0; });
        g_programs.clear();
    }
    g_clock_wakeup_interest.store(0, std::memory_order_release);
    g_worker_rescan_requested.store(0, std::memory_order_release);
}

bool needs_clock_anchor_wakeup() {
    return g_clock_wakeup_interest.load(std::memory_order_acquire) != 0;
}

int64_t next_deferred_retry_raw_ns(
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor) {
    if (g_worker_rescan_requested.exchange(0, std::memory_order_acq_rel) != 0)
        return realtime::monotonic_now_ns();
    const auto overlay = read_overlay_state();
    std::lock_guard<std::mutex> guard(g_registry_lock);
    int64_t next = 0;
    for (const auto &state : g_programs) {
        if (!state.deferred ||
            state.active.load(std::memory_order_acquire) == 0 ||
            state.presentation_enabled.load(std::memory_order_acquire) == 0)
            continue;
        const bool dependency_changed =
            state.deferred_input_generation != input.publication_generation ||
            state.deferred_clock_generation != anchor.publication_generation ||
            state.deferred_overlay_generation != overlay.generation;
        if (!dependency_changed || state.deferred_retry_raw_ns <= 0)
            continue;
        if (next == 0 || state.deferred_retry_raw_ns < next)
            next = state.deferred_retry_raw_ns;
    }
    return next;
}

void process_triggers(
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor) {
    const int64_t now_ns = realtime::monotonic_now_ns();
    const auto overlay = read_overlay_state();
    std::lock_guard<std::mutex> guard(g_registry_lock);
    for (size_t index = 0; index < g_programs.size(); ++index) {
        auto &state = g_programs[index];
        if (state.active.load(std::memory_order_acquire) == 0 ||
            state.presentation_enabled.load(std::memory_order_acquire) == 0)
            continue;

        if (state.last_session_generation != input.session_generation) {
            state.last_session_generation = input.session_generation;
            state.last_input_generation = 0;
            state.last_clock_generation = 0;
            state.last_overlay_generation = 0;
            state.bundle_load_attempted = false;
            state.deferred = false;
            state.deferred_retry_raw_ns = 0;
            rule_vm::reset_rule_runtime(state.runtime);
        }
        if (!should_retry_deferred(state, input, anchor, overlay, now_ns))
            continue;
        if (state.deferred) {
            state.deferred = false;
            state.deferred_retry_raw_ns = 0;
        }
        if (!dependency_available(state.descriptor, input, anchor))
            continue;

        bool due = false;
        switch (state.descriptor.trigger) {
        case pccompat_recipe::LifecycleTrigger::BundleLoad:
            due = !state.bundle_load_attempted;
            break;
        case pccompat_recipe::LifecycleTrigger::InputSnapshotChanged:
            due = state.last_input_generation != input.publication_generation;
            break;
        case pccompat_recipe::LifecycleTrigger::ClockAnchorChanged:
            due = state.last_clock_generation != anchor.publication_generation;
            break;
        case pccompat_recipe::LifecycleTrigger::OverlayStateChanged:
            due = overlay.available != 0 &&
                state.last_overlay_generation != overlay.generation;
            break;
        }
        if (!due)
            continue;

        if (!schedule_program_locked(index, state, input, anchor))
            continue;
        state.bundle_load_attempted =
            state.descriptor.trigger == pccompat_recipe::LifecycleTrigger::BundleLoad;
        state.last_input_generation = input.publication_generation;
        state.last_clock_generation = anchor.publication_generation;
        state.last_overlay_generation = overlay.generation;
    }
}

void publish_overlay_state(uint32_t generation, bool visible) {
    g_overlay_visible.store(visible ? 1u : 0u, std::memory_order_relaxed);
    g_overlay_generation.store(generation, std::memory_order_release);
    g_overlay_available.store(1u, std::memory_order_release);
    g_worker_rescan_requested.store(1u, std::memory_order_release);
    realtime::notify_waiters();
}

ExecutionOutcome execute_scheduled_task(
    const hud_logic::ScheduledPresentationTask &task,
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor,
    hud_logic::PresentationCommand &output) {
    ProgramState *state = nullptr;
    ExecutionLease lease;
    {
        std::lock_guard<std::mutex> guard(g_registry_lock);
        if (task.program_index >= g_programs.size())
            return ExecutionOutcome::NoOutput;
        state = &g_programs[task.program_index];
        if (state->bundle_id != task.generation ||
            state->active.load(std::memory_order_acquire) == 0 ||
            state->presentation_enabled.load(std::memory_order_acquire) == 0) {
            state->queued.store(0, std::memory_order_release);
            return ExecutionOutcome::NoOutput;
        }
        lease.acquire();
    }

    rule_vm::ExecutionResult result{};
    const auto overlay = read_overlay_state();
    const auto status = rule_vm::execute(
        rule_vm::ProgramView{
            state->instructions.data(),
            static_cast<uint32_t>(state->instructions.size()),
        },
        state->runtime,
        rule_vm::ExecutionContext{
            .realtime_now_ns = realtime::monotonic_now_ns(),
            .input = &input,
            .clock = &anchor,
            .touch_lane_count = 10,
            .overlay_available = overlay.available,
            .overlay_visible = overlay.visible,
        },
        result);

    state->queued.store(0, std::memory_order_release);
    if (state->active.load(std::memory_order_acquire) == 0 ||
        state->presentation_enabled.load(std::memory_order_acquire) == 0)
        return ExecutionOutcome::NoOutput;
    if (status == rule_vm::ExecutionStatus::Deferred) {
        std::lock_guard<std::mutex> guard(g_registry_lock);
        if (state->active.load(std::memory_order_acquire) == 0 ||
            state->presentation_enabled.load(std::memory_order_acquire) == 0)
            return ExecutionOutcome::NoOutput;
        state->deferred = true;
        state->deferred_input_generation = input.publication_generation;
        state->deferred_clock_generation = anchor.publication_generation;
        state->deferred_overlay_generation = overlay.generation;
        state->deferred_retry_raw_ns = saturating_add_ns(
            realtime::monotonic_now_ns(),
            state->descriptor.deferred_retry_delay_ns);
        g_clock_wakeup_interest.store(1, std::memory_order_release);
        g_worker_rescan_requested.store(1, std::memory_order_release);
        if (state->descriptor.trigger == pccompat_recipe::LifecycleTrigger::BundleLoad)
            state->bundle_load_attempted = false;
        return ExecutionOutcome::NoOutput;
    }
    if (status != rule_vm::ExecutionStatus::Completed) {
        if (status == rule_vm::ExecutionStatus::Disabled ||
            status == rule_vm::ExecutionStatus::VerificationFailed) {
            state->active.store(0, std::memory_order_release);
        }
        return ExecutionOutcome::NoOutput;
    }

    // clear() never frees ProgramState, so the pointer remains stable.  The
    // second active check above suppresses a task invalidated during execute.

    output = hud_logic::PresentationCommand{
        .sequence = task.sequence,
        .session_generation = task.session_generation,
        .generation = task.generation,
        .rule_id = task.rule_id,
        .command_type = task.command_type,
        .target_id = task.target_id,
        .payload0 = result.registers.integers[0],
        .payload1 = result.registers.integers[1],
        .value0 = static_cast<float>(result.registers.floats[0]),
        .value1 = static_cast<float>(result.registers.floats[1]),
    };
    return ExecutionOutcome::Produced;
}

size_t active_program_count() {
    std::lock_guard<std::mutex> guard(g_registry_lock);
    return static_cast<size_t>(std::count_if(
        g_programs.begin(),
        g_programs.end(),
        [](const ProgramState &state) {
            return state.active.load(std::memory_order_acquire) != 0 &&
                state.presentation_enabled.load(std::memory_order_acquire) != 0;
        }));
}

size_t registered_program_count() {
    std::lock_guard<std::mutex> guard(g_registry_lock);
    return static_cast<size_t>(std::count_if(
        g_programs.begin(),
        g_programs.end(),
        [](const ProgramState &state) { return state.bundle_id != 0; }));
}

}  // namespace starray::ui_recipe_runtime

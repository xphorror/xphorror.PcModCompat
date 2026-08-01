#pragma once

#include "hud_deadline_scheduler.h"
#include "pccompat_recipe_binary.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace starray::ui_recipe_runtime {

constexpr size_t kMaxPrograms = 256;

enum class ExecutionOutcome : uint8_t {
    NoOutput = 0,
    Produced = 1,
};

bool register_bundle(
    uint32_t bundle_id,
    const std::vector<rule_vm::Instruction> &bytecode,
    const std::vector<pccompat_recipe::LifecycleProgram> &programs,
    std::string &error);

bool set_bundle_presentation_enabled(uint32_t bundle_id, bool enabled);
bool retire_bundle(uint32_t bundle_id);

void clear();

// Used by the clock producer to avoid sleeping through a Deferred VM retry.
// This is an atomic hint only; process_triggers still owns the authoritative
// registry state.
bool needs_clock_anchor_wakeup();
int64_t next_deferred_retry_raw_ns(
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor);

void process_triggers(
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor);

// Called from verified fixed-op hooks. Only scalar state crosses into the
// worker; Unity objects remain owned by the UnityMain presentation sink.
void publish_overlay_state(uint32_t generation, bool visible);

ExecutionOutcome execute_scheduled_task(
    const hud_logic::ScheduledPresentationTask &task,
    const hud_logic::CompletedInputSnapshot &input,
    const hud_logic::ClockAnchorSnapshot &anchor,
    hud_logic::PresentationCommand &output);

size_t active_program_count();
size_t registered_program_count();

}  // namespace starray::ui_recipe_runtime

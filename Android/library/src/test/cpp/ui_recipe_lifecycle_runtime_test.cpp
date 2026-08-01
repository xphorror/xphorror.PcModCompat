#include "hud_deadline_scheduler.h"
#include "hud_logic_worker.h"
#include "native_rule_vm.h"
#include "pccompat_presentation_abi.h"
#include "realtime_event_core.h"
#include "ui_recipe_lifecycle_runtime.h"

#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <string>
#include <thread>
#include <vector>

namespace {

using starray::pccompat_recipe::LifecycleClockDomain;
using starray::pccompat_recipe::LifecycleFlags;
using starray::pccompat_recipe::LifecycleProgram;
using starray::pccompat_recipe::LifecycleTrigger;
using starray::rule_vm::Instruction;
using starray::rule_vm::Opcode;

Instruction instruction(Opcode opcode,
                        uint8_t dst = 0,
                        int64_t payload = 0) {
    return Instruction{
        .opcode = opcode,
        .dst = dst,
        .payload = payload,
    };
}

LifecycleProgram program(uint32_t rule_id,
                         LifecycleTrigger trigger,
                         uint32_t command_type,
                         uint32_t flags = 0) {
    return LifecycleProgram{
        .id = "test.lifecycle." + std::to_string(command_type),
        .runtime_rule_id = rule_id,
        .trigger = trigger,
        .clock_domain = LifecycleClockDomain::Realtime,
        .flags = flags,
        .program_start = 0,
        .program_count = 2,
        .instruction_budget = 32,
        .command_type = command_type,
        .target_id = command_type + 100,
        .initial_delay_ns = 0,
        .deferred_retry_delay_ns = 1,
    };
}

bool find_command(uint32_t command_type,
                  int64_t payload0,
                  float value0,
                  starray::hud_logic::PresentationSnapshot &snapshot) {
    if (!starray::hud_logic::read_latest_presentation_snapshot(snapshot))
        return false;
    for (uint32_t index = 0; index < snapshot.command_count; ++index) {
        const auto &command = snapshot.commands[index];
        if (command.command_type == command_type &&
            command.payload0 == payload0 &&
            std::fabs(command.value0 - value0) < 0.001f)
            return true;
    }
    return false;
}

bool wait_for_command(uint32_t command_type,
                      int64_t payload0,
                      float value0,
                      starray::hud_logic::PresentationSnapshot &snapshot) {
    for (int attempt = 0; attempt < 250; ++attempt) {
        if (find_command(command_type, payload0, value0, snapshot))
            return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return false;
}

void reset_runtime_state() {
    starray::ui_recipe_runtime::clear();
    starray::hud_logic::reset_presentation_scheduler_for_tests();
    starray::realtime::reset_for_tests();
}

void test_registration_is_all_or_nothing() {
    reset_runtime_state();
    const std::vector<Instruction> bytecode{
        instruction(Opcode::LoadConstI64, 0, 1),
        instruction(Opcode::Return),
    };
    auto valid = program(100, LifecycleTrigger::BundleLoad, 1001);
    auto invalid = valid;
    invalid.id = "test.invalid";
    invalid.program_start = 99;

    const auto before = starray::ui_recipe_runtime::registered_program_count();
    std::string error;
    assert(!starray::ui_recipe_runtime::register_bundle(
        100,
        bytecode,
        std::vector<LifecycleProgram>{valid, invalid},
        error));
    assert(starray::ui_recipe_runtime::registered_program_count() == before);
    assert(!error.empty());
}

void test_bundle_load_and_input_snapshot_outputs() {
    reset_runtime_state();

    const std::vector<Instruction> bundle_load_code{
        instruction(Opcode::LoadConstI64, 0, 42),
        instruction(Opcode::Return),
    };
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        101,
        bundle_load_code,
        std::vector<LifecycleProgram>{program(101, LifecycleTrigger::BundleLoad, 1002)},
        error));

    starray::hud_logic::PresentationSnapshot snapshot{};
    assert(wait_for_command(1002, 42, 0.0f, snapshot));
    assert(snapshot.command_count == 1);

    starray::realtime::begin_session(starray::realtime::monotonic_now_ns());
    assert(starray::realtime::observe_touch(
        0, 0, 1, 1, 100.0f, 100.0f, 1000, 1000));
    const std::vector<Instruction> input_code{
        instruction(Opcode::LoadInputTotal, 0),
        instruction(Opcode::Return),
    };
    assert(starray::ui_recipe_runtime::register_bundle(
        102,
        input_code,
        std::vector<LifecycleProgram>{program(
            102,
            LifecycleTrigger::InputSnapshotChanged,
            1003,
            starray::pccompat_recipe::LifecycleRequireInputSnapshot)},
        error));
    assert(wait_for_command(1003, 1, 0.0f, snapshot));
}

void test_deferred_clock_retries_after_anchor_generation_changes() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadSongPosition, 0),
        instruction(Opcode::Return),
    };
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        103,
        code,
        std::vector<LifecycleProgram>{program(
            103,
            LifecycleTrigger::ClockAnchorChanged,
            1004)},
        error));

    // The first execution is Deferred because no song clock is published.
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    starray::hud_logic::PresentationSnapshot snapshot{};
    assert(!find_command(1004, 0, 0.0f, snapshot));

    starray::hud_logic::ClockAnchorSnapshot anchor{};
    anchor.available = 1;
    anchor.valid_mask = starray::hud_logic::ClockAnchorSongPosition;
    anchor.song_position_seconds = 12.5;
    anchor.monotonic_raw_ns = starray::realtime::monotonic_now_ns();
    starray::hud_logic::publish_clock_anchor(anchor);
    assert(wait_for_command(1004, 0, 12.5f, snapshot));
}

void test_clear_invalidates_pending_and_published_commands() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadConstI64, 0, 55),
        instruction(Opcode::Return),
    };
    std::string error;
    auto delayed = program(104, LifecycleTrigger::BundleLoad, 1005);
    delayed.initial_delay_ns = 500'000'000;
    assert(starray::ui_recipe_runtime::register_bundle(
        104,
        code,
        std::vector<LifecycleProgram>{delayed},
        error));

    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    starray::ui_recipe_runtime::clear();
    starray::hud_logic::PresentationSnapshot snapshot{};
    assert(!starray::hud_logic::read_latest_presentation_snapshot(snapshot));
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    assert(!find_command(1005, 55, 0.0f, snapshot));
}

void test_versioned_presentation_abi_and_clear_generation() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadConstI64, 0, 77),
        instruction(Opcode::Return),
    };
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        105,
        code,
        std::vector<LifecycleProgram>{program(105, LifecycleTrigger::BundleLoad, 1006)},
        error));

    starray::hud_logic::PresentationSnapshot internal{};
    assert(wait_for_command(1006, 77, 0.0f, internal));

    PcCompatPresentationSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    snapshot.abi_version = PC_COMPAT_PRESENTATION_ABI_VERSION;
    assert(modmanager_pccompat_read_presentation_snapshot(
        &snapshot,
        sizeof(snapshot)) == 1);
    assert(snapshot.available == 1);
    assert(snapshot.command_count == 1);
    assert(snapshot.commands[0].command_type == 1006);
    assert(snapshot.commands[0].target_id == 1106);
    assert(snapshot.commands[0].payload0 == 77);

    const auto publication = snapshot.publication_generation;
    assert(modmanager_pccompat_read_presentation_snapshot(
        &snapshot,
        sizeof(snapshot)) == 0);

    starray::ui_recipe_runtime::clear();
    snapshot.struct_size = sizeof(snapshot);
    snapshot.abi_version = PC_COMPAT_PRESENTATION_ABI_VERSION;
    snapshot.publication_generation = publication;
    assert(modmanager_pccompat_read_presentation_snapshot(
        &snapshot,
        sizeof(snapshot)) == 1);
    assert(snapshot.available == 0);
    assert(snapshot.command_count == 0);
    assert(snapshot.publication_generation != publication);
}

void test_sink_reader_preserves_retained_generation_order() {
    reset_runtime_state();
    const std::vector<Instruction> first_code{
        instruction(Opcode::LoadConstI64, 0, 11),
        instruction(Opcode::Return),
    };
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        106,
        first_code,
        std::vector<LifecycleProgram>{program(106, LifecycleTrigger::BundleLoad, 1007)},
        error));
    starray::hud_logic::PresentationSnapshot latest{};
    assert(wait_for_command(1007, 11, 0.0f, latest));
    const auto first_publication = latest.publication_generation;

    const std::vector<Instruction> second_code{
        instruction(Opcode::LoadConstI64, 0, 22),
        instruction(Opcode::Return),
    };
    assert(starray::ui_recipe_runtime::register_bundle(
        107,
        second_code,
        std::vector<LifecycleProgram>{program(107, LifecycleTrigger::BundleLoad, 1008)},
        error));
    assert(wait_for_command(1008, 22, 0.0f, latest));
    assert(latest.publication_generation > first_publication);

    starray::hud_logic::PresentationSnapshot next{};
    assert(starray::hud_logic::read_next_presentation_snapshot(0, next));
    assert(next.publication_generation == first_publication);
    assert(next.commands[0].command_type == 1007);
    assert(starray::hud_logic::read_next_presentation_snapshot(
        next.publication_generation,
        next));
    assert(next.commands[0].command_type == 1008);
}

void test_sink_reader_preserves_more_than_three_publications() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadConstI64, 0, 33),
        instruction(Opcode::Return),
    };
    std::string error;
    starray::hud_logic::PresentationSnapshot latest{};
    for (uint32_t index = 0; index < 8; ++index) {
        const uint32_t command_type = 2000 + index;
        assert(starray::ui_recipe_runtime::register_bundle(
            200 + index,
            code,
            std::vector<LifecycleProgram>{program(
                200 + index,
                LifecycleTrigger::BundleLoad,
                command_type)},
            error));
        assert(wait_for_command(command_type, 33, 0.0f, latest));
    }

    uint32_t cursor = 0;
    for (uint32_t index = 0; index < 8; ++index) {
        starray::hud_logic::PresentationSnapshot next{};
        assert(starray::hud_logic::read_next_presentation_snapshot(cursor, next));
        assert(next.history_gap == 0);
        assert(next.commands[0].command_type == 2000 + index);
        cursor = next.publication_generation;
    }
}

void test_deferred_retry_delay_is_enforced() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadAudioPosition, 0),
        instruction(Opcode::Return),
    };
    auto delayed = program(300, LifecycleTrigger::BundleLoad, 3000);
    delayed.deferred_retry_delay_ns = 100'000'000;
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        300,
        code,
        std::vector<LifecycleProgram>{delayed},
        error));
    std::this_thread::sleep_for(std::chrono::milliseconds(10));

    starray::hud_logic::ClockAnchorSnapshot anchor{};
    anchor.available = 1;
    anchor.valid_mask = starray::hud_logic::ClockAnchorAudioPosition;
    anchor.audio_position_seconds = 9.5;
    anchor.monotonic_raw_ns = starray::realtime::monotonic_now_ns();
    starray::hud_logic::publish_clock_anchor(anchor);

    starray::hud_logic::PresentationSnapshot snapshot{};
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    assert(!find_command(3000, 0, 9.5f, snapshot));
    assert(wait_for_command(3000, 0, 9.5f, snapshot));
}

void test_overlay_state_trigger_drives_visibility_command() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadOverlayVisible, 0),
        instruction(Opcode::Return),
    };
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        350,
        code,
        std::vector<LifecycleProgram>{program(
            350,
            LifecycleTrigger::OverlayStateChanged,
            3500)},
        error));

    starray::hud_logic::PresentationSnapshot snapshot{};
    assert(!find_command(3500, 1, 0.0f, snapshot));
    starray::ui_recipe_runtime::publish_overlay_state(701, true);
    assert(wait_for_command(3500, 1, 0.0f, snapshot));

    starray::ui_recipe_runtime::publish_overlay_state(702, false);
    assert(wait_for_command(3500, 0, 0.0f, snapshot));
}

void test_history_overwrite_is_reported_as_a_gap() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadConstI64, 0, 44),
        instruction(Opcode::Return),
    };
    std::string error;
    starray::hud_logic::PresentationSnapshot latest{};
    assert(starray::ui_recipe_runtime::register_bundle(
        500,
        code,
        std::vector<LifecycleProgram>{program(500, LifecycleTrigger::BundleLoad, 5000)},
        error));
    assert(wait_for_command(5000, 44, 0.0f, latest));

    starray::hud_logic::PresentationSnapshot consumed{};
    assert(starray::hud_logic::read_next_presentation_snapshot(0, consumed));
    const uint32_t cursor = consumed.publication_generation;
    starray::hud_logic::acknowledge_presentation_generation(cursor);

    for (uint32_t index = 0;
         index <= starray::hud_logic::kPresentationSnapshotHistoryCapacity;
         ++index) {
        const uint32_t command_type = 5100 + index;
        assert(starray::ui_recipe_runtime::register_bundle(
            600 + index,
            code,
            std::vector<LifecycleProgram>{program(
                600 + index,
                LifecycleTrigger::BundleLoad,
                command_type)},
            error));
        assert(wait_for_command(command_type, 44, 0.0f, latest));
    }

    starray::hud_logic::PresentationSnapshot next{};
    assert(starray::hud_logic::read_next_presentation_snapshot(cursor, next));
    assert(next.history_gap != 0);
    assert(starray::hud_logic::presentation_history_overflow_count() > 0);
}

void test_clear_reclaims_lifecycle_registry_capacity() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadConstI64, 0, 1),
        instruction(Opcode::Return),
    };
    for (uint32_t index = 0; index < 300; ++index) {
        std::string error;
        assert(starray::ui_recipe_runtime::register_bundle(
            1000 + index,
            code,
            std::vector<LifecycleProgram>{program(
                1000 + index,
                LifecycleTrigger::BundleLoad,
                4000 + index)},
            error));
        assert(starray::ui_recipe_runtime::registered_program_count() == 1);
        starray::ui_recipe_runtime::clear();
        assert(starray::ui_recipe_runtime::registered_program_count() == 0);
    }
}

void test_bundle_presentation_gate_preserves_registered_program() {
    reset_runtime_state();
    const std::vector<Instruction> code{
        instruction(Opcode::LoadConstI64, 0, 1),
        instruction(Opcode::Return),
    };
    std::string error;
    assert(starray::ui_recipe_runtime::register_bundle(
        1500,
        code,
        std::vector<LifecycleProgram>{program(
            1500,
            LifecycleTrigger::BundleLoad,
            5500)},
        error));
    assert(starray::ui_recipe_runtime::registered_program_count() == 1);
    assert(starray::ui_recipe_runtime::set_bundle_presentation_enabled(1500, false));
    assert(starray::ui_recipe_runtime::active_program_count() == 0);
    assert(starray::ui_recipe_runtime::registered_program_count() == 1);
    assert(starray::ui_recipe_runtime::set_bundle_presentation_enabled(1500, true));
    assert(starray::ui_recipe_runtime::active_program_count() == 1);
}

}  // namespace

int main() {
    test_registration_is_all_or_nothing();
    test_bundle_load_and_input_snapshot_outputs();
    test_deferred_clock_retries_after_anchor_generation_changes();
    test_clear_invalidates_pending_and_published_commands();
    test_versioned_presentation_abi_and_clear_generation();
    test_sink_reader_preserves_retained_generation_order();
    test_sink_reader_preserves_more_than_three_publications();
    test_deferred_retry_delay_is_enforced();
    test_overlay_state_trigger_drives_visibility_command();
    test_history_overwrite_is_reported_as_a_gap();
    test_clear_reclaims_lifecycle_registry_capacity();
    test_bundle_presentation_gate_preserves_registered_program();
    return 0;
}

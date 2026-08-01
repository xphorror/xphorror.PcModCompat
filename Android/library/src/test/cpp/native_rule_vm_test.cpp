#include "native_rule_vm.h"

#include <array>
#include <cassert>
#include <cmath>
#include <cstdint>

namespace {

using starray::rule_vm::Instruction;
using starray::rule_vm::Opcode;

Instruction instruction(Opcode opcode,
                        uint8_t dst = 0,
                        uint8_t src0 = 0,
                        uint8_t src1 = 0,
                        int32_t immediate = 0,
                        int64_t payload = 0) {
    return Instruction{
        .opcode = opcode,
        .dst = dst,
        .src0 = src0,
        .src1 = src1,
        .immediate = immediate,
        .payload = payload,
    };
}

void test_loop_and_domain_reads() {
    using namespace starray;
    using namespace starray::rule_vm;
    reset_for_tests();

    const std::array program{
        instruction(Opcode::LoadConstI64, 0, 0, 0, 0, 0),
        instruction(Opcode::LoadConstI64, 1, 0, 0, 0, 0),
        instruction(Opcode::LoadConstI64, 2, 0, 0, 0, 5),
        instruction(Opcode::CompareLessI64, 0, 1, 2),
        instruction(Opcode::BranchIf, 0, 0, 0, 2),
        instruction(Opcode::Branch, 0, 0, 0, 5),
        instruction(Opcode::AddI64, 0, 0, 1),
        instruction(Opcode::LoadConstI64, 3, 0, 0, 0, 1),
        instruction(Opcode::AddI64, 1, 1, 3),
        instruction(Opcode::Branch, 0, 0, 0, -6),
        instruction(Opcode::LoadInputTotal, 4),
        instruction(Opcode::LoadSongPosition, 0),
        instruction(Opcode::LoadTouchLaneHeldMask, 5),
        instruction(Opcode::LoadConstI64, 6, 0, 0, 0, 4),
        instruction(Opcode::LoadTouchLaneHeldCount, 7, 6),
        instruction(Opcode::LoadTouchLaneTotalCount, 8, 6),
        instruction(Opcode::Return),
    };

    hud_logic::CompletedInputSnapshot input{};
    input.available = 1;
    input.total_count = 42;
    input.touch_lane_projections[4].lane_count = 10;
    input.touch_lane_projections[4].held_mask = 1u << 4;
    input.touch_lane_projections[4].held_counts[4] = 2;
    input.touch_lane_projections[4].total_counts[4] = 9;
    hud_logic::ClockAnchorSnapshot clock{};
    clock.available = 1;
    clock.valid_mask = hud_logic::ClockAnchorSongPosition;
    clock.song_position_seconds = 3.5;

    RuleRuntime runtime{};
    runtime.rule_id = 17;
    runtime.instruction_budget = 128;
    ExecutionResult result{};
    const auto status = execute(
        ProgramView{program.data(), static_cast<uint32_t>(program.size())},
        runtime,
        ExecutionContext{
            .realtime_now_ns = 9'000'000'000LL,
            .input = &input,
            .clock = &clock,
            .touch_lane_count = 10,
        },
        result);

    assert(status == ExecutionStatus::Completed);
    assert(result.registers.integers[0] == 10);
    assert(result.registers.integers[1] == 5);
    assert(result.registers.integers[4] == 42);
    assert(result.registers.integers[5] == (1u << 4));
    assert(result.registers.integers[7] == 2);
    assert(result.registers.integers[8] == 9);
    assert(std::fabs(result.registers.floats[0] - 3.5) < 0.001);
    assert(runtime.fault_count.load() == 0);
}

void test_budget_fault_ring_and_session_disable() {
    using namespace starray::rule_vm;
    reset_for_tests();

    const std::array program{
        instruction(Opcode::Branch, 0, 0, 0, 0),
    };
    RuleRuntime runtime{};
    runtime.rule_id = 23;
    runtime.instruction_budget = 4;

    for (uint32_t attempt = 0; attempt < kDisableAfterFaultCount; ++attempt) {
        ExecutionResult result{};
        assert(execute(
            ProgramView{program.data(), static_cast<uint32_t>(program.size())},
            runtime,
            ExecutionContext{},
            result) == ExecutionStatus::Faulted);
        assert(result.exception.code == ExceptionCode::BudgetExhausted);
    }
    assert(runtime.disabled.load() == 1);

    ExecutionResult disabled_result{};
    assert(execute(
        ProgramView{program.data(), static_cast<uint32_t>(program.size())},
        runtime,
        ExecutionContext{},
        disabled_result) == ExecutionStatus::Disabled);

    std::array<FaultRecord, 8> faults{};
    const auto read = read_faults(0, faults.data(), faults.size());
    assert(read.count == kDisableAfterFaultCount);
    assert(read.cursor == kDisableAfterFaultCount);
    assert(faults[0].rule_id == 23);
    assert(faults[0].code == static_cast<uint32_t>(ExceptionCode::BudgetExhausted));
    assert(faults[2].count == kDisableAfterFaultCount);

    reset_rule_runtime(runtime);
    assert(runtime.disabled.load() == 0);
    assert(runtime.fault_count.load() == 0);
}

void test_verifier_and_divide_by_zero() {
    using namespace starray::rule_vm;
    reset_for_tests();

    const std::array invalid_program{
        instruction(Opcode::Branch, 0, 0, 0, 2),
        instruction(Opcode::Return),
    };
    Exception error{};
    assert(!verify_program(
        ProgramView{invalid_program.data(), static_cast<uint32_t>(invalid_program.size())},
        error));
    assert(error.code == ExceptionCode::InvalidBranch);

    const std::array divide_program{
        instruction(Opcode::LoadConstI64, 0, 0, 0, 0, 9),
        instruction(Opcode::LoadConstI64, 1, 0, 0, 0, 0),
        instruction(Opcode::DivI64, 2, 0, 1),
        instruction(Opcode::Return),
    };
    RuleRuntime runtime{};
    runtime.rule_id = 31;
    ExecutionResult result{};
    assert(execute(
        ProgramView{divide_program.data(), static_cast<uint32_t>(divide_program.size())},
        runtime,
        ExecutionContext{},
        result) == ExecutionStatus::Faulted);
    assert(result.exception.code == ExceptionCode::DivideByZero);

    const std::array clock_program{
        instruction(Opcode::LoadUnityScaledTime, 0),
        instruction(Opcode::Return),
    };
    RuleRuntime deferred_runtime{};
    deferred_runtime.rule_id = 32;
    ExecutionResult deferred_result{};
    assert(execute(
        ProgramView{clock_program.data(), static_cast<uint32_t>(clock_program.size())},
        deferred_runtime,
        ExecutionContext{},
        deferred_result) == ExecutionStatus::Deferred);
    assert(deferred_result.exception.code == ExceptionCode::MissingClockDomain);
    assert(deferred_runtime.fault_count.load() == 0);
    assert(deferred_runtime.disabled.load() == 0);

    const std::array overlay_program{
        instruction(Opcode::LoadOverlayVisible, 0),
        instruction(Opcode::Return),
    };
    RuleRuntime overlay_runtime{};
    overlay_runtime.rule_id = 33;
    ExecutionResult overlay_result{};
    assert(execute(
        ProgramView{overlay_program.data(), static_cast<uint32_t>(overlay_program.size())},
        overlay_runtime,
        ExecutionContext{},
        overlay_result) == ExecutionStatus::Deferred);
    assert(overlay_result.exception.code == ExceptionCode::MissingOverlayState);

    overlay_result = {};
    assert(execute(
        ProgramView{overlay_program.data(), static_cast<uint32_t>(overlay_program.size())},
        overlay_runtime,
        ExecutionContext{.overlay_available = 1, .overlay_visible = 1},
        overlay_result) == ExecutionStatus::Completed);
    assert(overlay_result.registers.integers[0] == 1);
}

}  // namespace

int main() {
    test_loop_and_domain_reads();
    test_budget_fault_ring_and_session_disable();
    test_verifier_and_divide_by_zero();
    return 0;
}

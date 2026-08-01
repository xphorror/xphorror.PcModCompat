#pragma once

#include "hud_logic_worker.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace starray::rule_vm {

constexpr size_t kIntegerRegisterCount = 32;
constexpr size_t kFloatRegisterCount = 16;
constexpr size_t kPredicateRegisterCount = 16;
constexpr size_t kMaxProgramInstructions = 4096;
constexpr uint32_t kDefaultInstructionBudget = 1024;
constexpr uint32_t kDisableAfterFaultCount = 3;

enum class Opcode : uint8_t {
    Nop = 0,
    LoadConstI64,
    LoadConstF64,
    MoveI64,
    MoveF64,
    AddI64,
    SubI64,
    MulI64,
    DivI64,
    AddF64,
    SubF64,
    MulF64,
    DivF64,
    CompareEqualI64,
    CompareLessI64,
    CompareEqualF64,
    CompareLessF64,
    NotPredicate,
    AndPredicate,
    OrPredicate,
    Branch,
    BranchIf,
    LoadRealtimeNs,
    LoadInputTotal,
    LoadInputKps,
    LoadInputHeldMask,
    LoadTouchLaneHeldMask,
    LoadTouchLaneHeldCount,
    LoadTouchLaneTotalCount,
    LoadUnityScaledTime,
    LoadUnityTimeScale,
    LoadUnityFrameCount,
    LoadSongPosition,
    LoadAudioPosition,
    LoadMapPosition,
    Return,
    LoadOverlayVisible,
};

struct Instruction {
    Opcode opcode = Opcode::Nop;
    uint8_t dst = 0;
    uint8_t src0 = 0;
    uint8_t src1 = 0;
    int32_t immediate = 0;
    int64_t payload = 0;
};

static_assert(sizeof(Instruction) == 16);

struct ProgramView {
    const Instruction *instructions = nullptr;
    uint32_t instruction_count = 0;
};

enum class ExceptionCode : uint32_t {
    None = 0,
    BudgetExhausted,
    InvalidProgram,
    InvalidRegister,
    InvalidBranch,
    DivideByZero,
    ArithmeticOverflow,
    MissingInputSnapshot,
    MissingClockDomain,
    OutOfBounds,
    UnsupportedOpcode,
    MissingOverlayState,
};

struct Exception {
    ExceptionCode code = ExceptionCode::None;
    uint32_t rule_id = 0;
    uint32_t pc = 0;
    uint32_t opcode = 0;
    std::array<char, 160> message{};
};

enum class ExecutionStatus : uint32_t {
    Completed = 0,
    Deferred,
    Faulted,
    Disabled,
    VerificationFailed,
};

struct RuleRuntime {
    uint32_t rule_id = 0;
    uint32_t instruction_budget = kDefaultInstructionBudget;
    std::atomic<uint32_t> fault_count{0};
    std::atomic<uint32_t> disabled{0};
    std::atomic<uintptr_t> verified_program{0};
    std::atomic<uint32_t> verified_instruction_count{0};
};

struct ExecutionContext {
    int64_t realtime_now_ns = 0;
    const hud_logic::CompletedInputSnapshot *input = nullptr;
    const hud_logic::ClockAnchorSnapshot *clock = nullptr;
    uint32_t touch_lane_count = 10;
    uint32_t overlay_available = 0;
    uint32_t overlay_visible = 0;
};

struct RegisterFile {
    std::array<int64_t, kIntegerRegisterCount> integers{};
    std::array<double, kFloatRegisterCount> floats{};
    std::array<uint8_t, kPredicateRegisterCount> predicates{};
};

struct ExecutionResult {
    ExecutionStatus status = ExecutionStatus::Completed;
    uint32_t executed_instructions = 0;
    uint32_t final_pc = 0;
    RegisterFile registers{};
    Exception exception{};
};

struct FaultRecord {
    uint64_t sequence = 0;
    int64_t timestamp_ns = 0;
    uint32_t rule_id = 0;
    uint32_t code = 0;
    uint32_t pc = 0;
    uint32_t opcode = 0;
    uint32_t count = 0;
    std::array<char, 160> message{};
};

struct FaultReadResult {
    size_t count = 0;
    uint64_t cursor = 0;
    uint64_t dropped_before_cursor = 0;
};

bool verify_program(ProgramView program, Exception &error);

ExecutionStatus execute(ProgramView program,
                        RuleRuntime &runtime,
                        const ExecutionContext &context,
                        ExecutionResult &result);

FaultReadResult read_faults(uint64_t cursor,
                            FaultRecord *output,
                            size_t capacity);

void reset_rule_runtime(RuleRuntime &runtime);

void reset_for_tests();

}  // namespace starray::rule_vm

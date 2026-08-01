#include "native_rule_vm.h"

#include "realtime_event_core.h"

#include <algorithm>
#include <array>
#include <climits>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>

#if defined(__ANDROID__)
#include <android/log.h>
#endif

namespace starray::rule_vm {
namespace {

constexpr size_t kFaultCapacity = 64;

struct FaultRing {
    std::mutex lock;
    std::array<FaultRecord, kFaultCapacity> values{};
    size_t head = 0;
    size_t count = 0;
    uint64_t next_sequence = 1;
    uint64_t dropped = 0;
};

FaultRing g_faults;

bool valid_integer_register(uint8_t index) {
    return index < kIntegerRegisterCount;
}

bool valid_float_register(uint8_t index) {
    return index < kFloatRegisterCount;
}

bool valid_predicate_register(uint8_t index) {
    return index < kPredicateRegisterCount;
}

double decode_float64(int64_t payload) {
    const uint64_t bits = static_cast<uint64_t>(payload);
    double value = 0.0;
    static_assert(sizeof(value) == sizeof(bits));
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

void set_exception(Exception &error,
                   ExceptionCode code,
                   uint32_t rule_id,
                   uint32_t pc,
                   Opcode opcode,
                   const char *message) {
    error = {};
    error.code = code;
    error.rule_id = rule_id;
    error.pc = pc;
    error.opcode = static_cast<uint32_t>(opcode);
    if (message != nullptr)
        std::snprintf(error.message.data(), error.message.size(), "%s", message);
}

void append_fault(const Exception &error, uint32_t count) {
    std::lock_guard<std::mutex> guard(g_faults.lock);
    if (g_faults.count == g_faults.values.size()) {
        g_faults.head = (g_faults.head + 1) % g_faults.values.size();
        --g_faults.count;
        ++g_faults.dropped;
    }

    const size_t tail = (g_faults.head + g_faults.count) % g_faults.values.size();
    auto &fault = g_faults.values[tail];
    fault = {};
    fault.sequence = g_faults.next_sequence++;
    fault.timestamp_ns = realtime::monotonic_now_ns();
    fault.rule_id = error.rule_id;
    fault.code = static_cast<uint32_t>(error.code);
    fault.pc = error.pc;
    fault.opcode = error.opcode;
    fault.count = count;
    fault.message = error.message;
    ++g_faults.count;

#if defined(__ANDROID__)
    __android_log_print(
        ANDROID_LOG_ERROR,
        "StArray.RuleVM",
        "rule=%u fault=%u count=%u pc=%u opcode=%u message=%s",
        error.rule_id,
        static_cast<uint32_t>(error.code),
        count,
        error.pc,
        error.opcode,
        error.message.data());
#endif
}

bool verify_branch(const ProgramView &program,
                   uint32_t pc,
                   const Instruction &instruction,
                   Exception &error) {
    const int64_t target = static_cast<int64_t>(pc) + instruction.immediate;
    if (target < 0 || target >= program.instruction_count) {
        set_exception(
            error,
            ExceptionCode::InvalidBranch,
            0,
            pc,
            instruction.opcode,
            "branch target is outside the program");
        return false;
    }
    return true;
}

bool verify_instruction(const ProgramView &program,
                        uint32_t pc,
                        const Instruction &instruction,
                        Exception &error) {
    switch (instruction.opcode) {
        case Opcode::Nop:
        case Opcode::Return:
            return true;
        case Opcode::LoadConstI64:
        case Opcode::LoadRealtimeNs:
        case Opcode::LoadInputTotal:
        case Opcode::LoadInputHeldMask:
        case Opcode::LoadTouchLaneHeldMask:
        case Opcode::LoadUnityFrameCount:
        case Opcode::LoadOverlayVisible:
            return valid_integer_register(instruction.dst);
        case Opcode::LoadTouchLaneHeldCount:
        case Opcode::LoadTouchLaneTotalCount:
            return valid_integer_register(instruction.dst) &&
                valid_integer_register(instruction.src0);
        case Opcode::LoadConstF64:
        case Opcode::LoadInputKps:
        case Opcode::LoadUnityScaledTime:
        case Opcode::LoadUnityTimeScale:
        case Opcode::LoadSongPosition:
        case Opcode::LoadAudioPosition:
        case Opcode::LoadMapPosition:
            return valid_float_register(instruction.dst);
        case Opcode::MoveI64:
            return valid_integer_register(instruction.dst) &&
                valid_integer_register(instruction.src0);
        case Opcode::MoveF64:
            return valid_float_register(instruction.dst) &&
                valid_float_register(instruction.src0);
        case Opcode::AddI64:
        case Opcode::SubI64:
        case Opcode::MulI64:
        case Opcode::DivI64:
            return valid_integer_register(instruction.dst) &&
                valid_integer_register(instruction.src0) &&
                valid_integer_register(instruction.src1);
        case Opcode::AddF64:
        case Opcode::SubF64:
        case Opcode::MulF64:
        case Opcode::DivF64:
            return valid_float_register(instruction.dst) &&
                valid_float_register(instruction.src0) &&
                valid_float_register(instruction.src1);
        case Opcode::CompareEqualI64:
        case Opcode::CompareLessI64:
            return valid_predicate_register(instruction.dst) &&
                valid_integer_register(instruction.src0) &&
                valid_integer_register(instruction.src1);
        case Opcode::CompareEqualF64:
        case Opcode::CompareLessF64:
            return valid_predicate_register(instruction.dst) &&
                valid_float_register(instruction.src0) &&
                valid_float_register(instruction.src1);
        case Opcode::NotPredicate:
            return valid_predicate_register(instruction.dst) &&
                valid_predicate_register(instruction.src0);
        case Opcode::AndPredicate:
        case Opcode::OrPredicate:
            return valid_predicate_register(instruction.dst) &&
                valid_predicate_register(instruction.src0) &&
                valid_predicate_register(instruction.src1);
        case Opcode::Branch:
            return verify_branch(program, pc, instruction, error);
        case Opcode::BranchIf:
            return valid_predicate_register(instruction.src0) &&
                verify_branch(program, pc, instruction, error);
    }

    set_exception(
        error,
        ExceptionCode::UnsupportedOpcode,
        0,
        pc,
        instruction.opcode,
        "unsupported opcode");
    return false;
}

ExecutionStatus fault(RuleRuntime &runtime,
                      ExecutionResult &result,
                      ExceptionCode code,
                      uint32_t pc,
                      Opcode opcode,
                      const char *message) {
    set_exception(result.exception, code, runtime.rule_id, pc, opcode, message);
    result.status = ExecutionStatus::Faulted;
    result.final_pc = pc;
    const uint32_t count = runtime.fault_count.fetch_add(1, std::memory_order_acq_rel) + 1;
    if (count >= kDisableAfterFaultCount)
        runtime.disabled.store(1, std::memory_order_release);
    append_fault(result.exception, count);
    return result.status;
}

ExecutionStatus defer(ExecutionResult &result,
                      ExceptionCode code,
                      uint32_t rule_id,
                      uint32_t pc,
                      Opcode opcode,
                      const char *message) {
    set_exception(result.exception, code, rule_id, pc, opcode, message);
    result.status = ExecutionStatus::Deferred;
    result.final_pc = pc;
    return result.status;
}

bool clock_valid(const ExecutionContext &context, uint32_t mask) {
    return context.clock != nullptr &&
        context.clock->available != 0 &&
        (context.input == nullptr ||
         context.input->session_generation == context.clock->session_generation) &&
        (context.clock->valid_mask & mask) == mask;
}

}  // namespace

bool verify_program(ProgramView program, Exception &error) {
    error = {};
    if (program.instructions == nullptr ||
        program.instruction_count == 0 ||
        program.instruction_count > kMaxProgramInstructions) {
        set_exception(
            error,
            ExceptionCode::InvalidProgram,
            0,
            0,
            Opcode::Nop,
            "program is empty or exceeds the instruction limit");
        return false;
    }

    for (uint32_t pc = 0; pc < program.instruction_count; ++pc) {
        if (!verify_instruction(program, pc, program.instructions[pc], error)) {
            if (error.code == ExceptionCode::None) {
                set_exception(
                    error,
                    ExceptionCode::InvalidRegister,
                    0,
                    pc,
                    program.instructions[pc].opcode,
                    "register index is outside the register file");
            }
            return false;
        }
    }
    return true;
}

ExecutionStatus execute(ProgramView program,
                        RuleRuntime &runtime,
                        const ExecutionContext &context,
                        ExecutionResult &result) {
    result = {};
    if (runtime.disabled.load(std::memory_order_acquire) != 0) {
        result.status = ExecutionStatus::Disabled;
        return result.status;
    }

    const uintptr_t program_identity = reinterpret_cast<uintptr_t>(program.instructions);
    const bool already_verified =
        runtime.verified_program.load(std::memory_order_acquire) == program_identity &&
        runtime.verified_instruction_count.load(std::memory_order_acquire) ==
            program.instruction_count;
    Exception verification_error{};
    if (!already_verified && !verify_program(program, verification_error)) {
        verification_error.rule_id = runtime.rule_id;
        result.status = ExecutionStatus::VerificationFailed;
        result.exception = verification_error;
        const uint32_t count = runtime.fault_count.fetch_add(1, std::memory_order_acq_rel) + 1;
        if (count >= kDisableAfterFaultCount)
            runtime.disabled.store(1, std::memory_order_release);
        append_fault(result.exception, count);
        return result.status;
    }
    if (!already_verified) {
        runtime.verified_instruction_count.store(
            program.instruction_count,
            std::memory_order_release);
        runtime.verified_program.store(program_identity, std::memory_order_release);
    }

    const uint32_t budget = runtime.instruction_budget == 0
        ? kDefaultInstructionBudget
        : runtime.instruction_budget;
    const hud_logic::TouchLaneProjectionSnapshot *touch_projection = nullptr;
    if (context.input != nullptr && context.input->available != 0) {
        for (const auto &candidate : context.input->touch_lane_projections) {
            if (candidate.lane_count == context.touch_lane_count) {
                touch_projection = &candidate;
                break;
            }
        }
    }
    uint32_t pc = 0;
    while (pc < program.instruction_count) {
        if (result.executed_instructions >= budget) {
            return fault(
                runtime,
                result,
                ExceptionCode::BudgetExhausted,
                pc,
                program.instructions[pc].opcode,
                "instruction budget exhausted");
        }

        const auto &instruction = program.instructions[pc];
        ++result.executed_instructions;
        bool advance = true;
        switch (instruction.opcode) {
            case Opcode::Nop:
                break;
            case Opcode::LoadConstI64:
                result.registers.integers[instruction.dst] = instruction.payload;
                break;
            case Opcode::LoadConstF64:
                result.registers.floats[instruction.dst] = decode_float64(instruction.payload);
                break;
            case Opcode::MoveI64:
                result.registers.integers[instruction.dst] =
                    result.registers.integers[instruction.src0];
                break;
            case Opcode::MoveF64:
                result.registers.floats[instruction.dst] =
                    result.registers.floats[instruction.src0];
                break;
            case Opcode::AddI64:
                result.registers.integers[instruction.dst] = static_cast<int64_t>(
                    static_cast<uint64_t>(result.registers.integers[instruction.src0]) +
                    static_cast<uint64_t>(result.registers.integers[instruction.src1]));
                break;
            case Opcode::SubI64:
                result.registers.integers[instruction.dst] = static_cast<int64_t>(
                    static_cast<uint64_t>(result.registers.integers[instruction.src0]) -
                    static_cast<uint64_t>(result.registers.integers[instruction.src1]));
                break;
            case Opcode::MulI64:
                result.registers.integers[instruction.dst] = static_cast<int64_t>(
                    static_cast<uint64_t>(result.registers.integers[instruction.src0]) *
                    static_cast<uint64_t>(result.registers.integers[instruction.src1]));
                break;
            case Opcode::DivI64: {
                const int64_t lhs = result.registers.integers[instruction.src0];
                const int64_t rhs = result.registers.integers[instruction.src1];
                if (rhs == 0) {
                    return fault(
                        runtime,
                        result,
                        ExceptionCode::DivideByZero,
                        pc,
                        instruction.opcode,
                        "integer divide by zero");
                }
                if (lhs == INT64_MIN && rhs == -1) {
                    return fault(
                        runtime,
                        result,
                        ExceptionCode::ArithmeticOverflow,
                        pc,
                        instruction.opcode,
                        "integer division overflow");
                }
                result.registers.integers[instruction.dst] = lhs / rhs;
                break;
            }
            case Opcode::AddF64:
                result.registers.floats[instruction.dst] =
                    result.registers.floats[instruction.src0] +
                    result.registers.floats[instruction.src1];
                break;
            case Opcode::SubF64:
                result.registers.floats[instruction.dst] =
                    result.registers.floats[instruction.src0] -
                    result.registers.floats[instruction.src1];
                break;
            case Opcode::MulF64:
                result.registers.floats[instruction.dst] =
                    result.registers.floats[instruction.src0] *
                    result.registers.floats[instruction.src1];
                break;
            case Opcode::DivF64:
                result.registers.floats[instruction.dst] =
                    result.registers.floats[instruction.src0] /
                    result.registers.floats[instruction.src1];
                break;
            case Opcode::CompareEqualI64:
                result.registers.predicates[instruction.dst] =
                    result.registers.integers[instruction.src0] ==
                    result.registers.integers[instruction.src1];
                break;
            case Opcode::CompareLessI64:
                result.registers.predicates[instruction.dst] =
                    result.registers.integers[instruction.src0] <
                    result.registers.integers[instruction.src1];
                break;
            case Opcode::CompareEqualF64:
                result.registers.predicates[instruction.dst] =
                    result.registers.floats[instruction.src0] ==
                    result.registers.floats[instruction.src1];
                break;
            case Opcode::CompareLessF64:
                result.registers.predicates[instruction.dst] =
                    result.registers.floats[instruction.src0] <
                    result.registers.floats[instruction.src1];
                break;
            case Opcode::NotPredicate:
                result.registers.predicates[instruction.dst] =
                    result.registers.predicates[instruction.src0] == 0;
                break;
            case Opcode::AndPredicate:
                result.registers.predicates[instruction.dst] =
                    result.registers.predicates[instruction.src0] != 0 &&
                    result.registers.predicates[instruction.src1] != 0;
                break;
            case Opcode::OrPredicate:
                result.registers.predicates[instruction.dst] =
                    result.registers.predicates[instruction.src0] != 0 ||
                    result.registers.predicates[instruction.src1] != 0;
                break;
            case Opcode::Branch:
                pc = static_cast<uint32_t>(static_cast<int64_t>(pc) + instruction.immediate);
                advance = false;
                break;
            case Opcode::BranchIf:
                if (result.registers.predicates[instruction.src0] != 0) {
                    pc = static_cast<uint32_t>(static_cast<int64_t>(pc) + instruction.immediate);
                    advance = false;
                }
                break;
            case Opcode::LoadRealtimeNs:
                result.registers.integers[instruction.dst] = context.realtime_now_ns > 0
                    ? context.realtime_now_ns
                    : realtime::monotonic_now_ns();
                break;
            case Opcode::LoadInputTotal:
                if (context.input == nullptr || context.input->available == 0) {
                    return defer(result, ExceptionCode::MissingInputSnapshot, runtime.rule_id,
                                 pc, instruction.opcode, "input snapshot is unavailable");
                }
                result.registers.integers[instruction.dst] = context.input->total_count;
                break;
            case Opcode::LoadInputKps:
                if (context.input == nullptr || context.input->available == 0) {
                    return defer(result, ExceptionCode::MissingInputSnapshot, runtime.rule_id,
                                 pc, instruction.opcode, "input snapshot is unavailable");
                }
                result.registers.floats[instruction.dst] = context.input->kps;
                break;
            case Opcode::LoadInputHeldMask:
                if (context.input == nullptr || context.input->available == 0) {
                    return defer(result, ExceptionCode::MissingInputSnapshot, runtime.rule_id,
                                 pc, instruction.opcode, "input snapshot is unavailable");
                }
                result.registers.integers[instruction.dst] = context.input->held_mask;
                break;
            case Opcode::LoadTouchLaneHeldMask:
                if (context.input == nullptr || context.input->available == 0) {
                    return defer(result, ExceptionCode::MissingInputSnapshot, runtime.rule_id,
                                 pc, instruction.opcode, "input snapshot is unavailable");
                }
                if (touch_projection == nullptr) {
                    return fault(runtime, result, ExceptionCode::OutOfBounds, pc,
                                 instruction.opcode, "touch lane projection is unavailable");
                }
                result.registers.integers[instruction.dst] = touch_projection->held_mask;
                break;
            case Opcode::LoadTouchLaneHeldCount:
            case Opcode::LoadTouchLaneTotalCount: {
                if (context.input == nullptr || context.input->available == 0) {
                    return defer(result, ExceptionCode::MissingInputSnapshot, runtime.rule_id,
                                 pc, instruction.opcode, "input snapshot is unavailable");
                }
                if (touch_projection == nullptr) {
                    return fault(runtime, result, ExceptionCode::OutOfBounds, pc,
                                 instruction.opcode, "touch lane projection is unavailable");
                }
                const int64_t lane = result.registers.integers[instruction.src0];
                if (lane < 0 || lane >= touch_projection->lane_count) {
                    return fault(runtime, result, ExceptionCode::OutOfBounds, pc,
                                 instruction.opcode, "touch lane index is outside the projection");
                }
                const size_t lane_index = static_cast<size_t>(lane);
                result.registers.integers[instruction.dst] =
                    instruction.opcode == Opcode::LoadTouchLaneHeldCount
                        ? touch_projection->held_counts[lane_index]
                        : touch_projection->total_counts[lane_index];
                break;
            }
            case Opcode::LoadUnityScaledTime:
                if (!clock_valid(context, hud_logic::ClockAnchorUnityScaled)) {
                    return defer(result, ExceptionCode::MissingClockDomain, runtime.rule_id,
                                 pc, instruction.opcode, "Unity scaled clock is unavailable");
                }
                result.registers.floats[instruction.dst] = context.clock->unity_scaled_seconds;
                break;
            case Opcode::LoadUnityTimeScale:
                if (!clock_valid(context, hud_logic::ClockAnchorUnityScaled)) {
                    return defer(result, ExceptionCode::MissingClockDomain, runtime.rule_id,
                                 pc, instruction.opcode, "Unity scaled clock is unavailable");
                }
                result.registers.floats[instruction.dst] = context.clock->unity_time_scale;
                break;
            case Opcode::LoadUnityFrameCount:
                if (!clock_valid(context, hud_logic::ClockAnchorFrameCount)) {
                    return defer(result, ExceptionCode::MissingClockDomain, runtime.rule_id,
                                 pc, instruction.opcode, "Unity frame clock is unavailable");
                }
                result.registers.integers[instruction.dst] = context.clock->frame_count;
                break;
            case Opcode::LoadSongPosition:
                if (!clock_valid(context, hud_logic::ClockAnchorSongPosition)) {
                    return defer(result, ExceptionCode::MissingClockDomain, runtime.rule_id,
                                 pc, instruction.opcode, "song clock is unavailable");
                }
                result.registers.floats[instruction.dst] = context.clock->song_position_seconds;
                break;
            case Opcode::LoadAudioPosition:
                if (!clock_valid(context, hud_logic::ClockAnchorAudioPosition)) {
                    return defer(result, ExceptionCode::MissingClockDomain, runtime.rule_id,
                                 pc, instruction.opcode, "audio clock is unavailable");
                }
                result.registers.floats[instruction.dst] = context.clock->audio_position_seconds;
                break;
            case Opcode::LoadMapPosition:
                if (!clock_valid(context, hud_logic::ClockAnchorMapPosition)) {
                    return defer(result, ExceptionCode::MissingClockDomain, runtime.rule_id,
                                 pc, instruction.opcode, "map clock is unavailable");
                }
                result.registers.floats[instruction.dst] = context.clock->map_position_seconds;
                break;
            case Opcode::LoadOverlayVisible:
                if (context.overlay_available == 0) {
                    return defer(result, ExceptionCode::MissingOverlayState, runtime.rule_id,
                                 pc, instruction.opcode, "overlay state is unavailable");
                }
                result.registers.integers[instruction.dst] =
                    context.overlay_visible != 0 ? 1 : 0;
                break;
            case Opcode::Return:
                result.status = ExecutionStatus::Completed;
                result.final_pc = pc;
                return result.status;
        }

        if (advance)
            ++pc;
    }

    return fault(
        runtime,
        result,
        ExceptionCode::InvalidProgram,
        pc,
        Opcode::Nop,
        "program terminated without Return");
}

FaultReadResult read_faults(uint64_t cursor,
                            FaultRecord *output,
                            size_t capacity) {
    std::lock_guard<std::mutex> guard(g_faults.lock);
    FaultReadResult result{};
    result.cursor = cursor;
    if (output == nullptr || capacity == 0 || g_faults.count == 0)
        return result;

    const uint64_t oldest_sequence = g_faults.values[g_faults.head].sequence;
    uint64_t requested_sequence = cursor + 1;
    if (requested_sequence < oldest_sequence) {
        result.dropped_before_cursor = oldest_sequence - requested_sequence;
        requested_sequence = oldest_sequence;
    }

    for (size_t offset = 0; offset < g_faults.count && result.count < capacity; ++offset) {
        const auto &fault = g_faults.values[(g_faults.head + offset) % g_faults.values.size()];
        if (fault.sequence < requested_sequence)
            continue;
        output[result.count++] = fault;
        result.cursor = fault.sequence;
    }
    return result;
}

void reset_rule_runtime(RuleRuntime &runtime) {
    runtime.fault_count.store(0, std::memory_order_release);
    runtime.disabled.store(0, std::memory_order_release);
    runtime.verified_instruction_count.store(0, std::memory_order_release);
    runtime.verified_program.store(0, std::memory_order_release);
}

void reset_for_tests() {
    std::lock_guard<std::mutex> guard(g_faults.lock);
    g_faults.values = {};
    g_faults.head = 0;
    g_faults.count = 0;
    g_faults.next_sequence = 1;
    g_faults.dropped = 0;
}

}  // namespace starray::rule_vm

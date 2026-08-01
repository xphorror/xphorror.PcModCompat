#include "pccompat_presentation_abi.h"

#include "hud_deadline_scheduler.h"

#include <cstddef>
#include <cstring>

static_assert(sizeof(PcCompatPresentationCommandV1) == 56);
static_assert(sizeof(PcCompatPresentationSnapshotV1) == 3636);

extern "C" int modmanager_pccompat_read_presentation_snapshot(
    void *output,
    uint32_t output_size) {
    if (output == nullptr || output_size < sizeof(PcCompatPresentationSnapshotV1))
        return -1;

    const auto *request = static_cast<const PcCompatPresentationSnapshotV1 *>(output);
    if (request->struct_size != sizeof(PcCompatPresentationSnapshotV1) ||
        request->abi_version != PC_COMPAT_PRESENTATION_ABI_VERSION) {
        return -1;
    }

    starray::hud_logic::PresentationSnapshot source{};
    if (!starray::hud_logic::read_presentation_snapshot(
            request->publication_generation,
            source)) {
        return 0;
    }

    PcCompatPresentationSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(PcCompatPresentationSnapshotV1);
    snapshot.abi_version = PC_COMPAT_PRESENTATION_ABI_VERSION;
    snapshot.publication_generation = source.publication_generation;
    snapshot.session_generation = source.session_generation;
    snapshot.available = source.available;
    snapshot.dropped_stale_tasks = source.dropped_stale_tasks;
    snapshot.scheduler_overflow_count = source.scheduler_overflow_count;
    snapshot.published_raw_ns = source.published_raw_ns;

    const auto count = source.command_count < PC_COMPAT_PRESENTATION_MAX_COMMANDS
        ? source.command_count
        : PC_COMPAT_PRESENTATION_MAX_COMMANDS;
    snapshot.command_count = count;
    for (uint32_t index = 0; index < count; ++index) {
        const auto &command = source.commands[index];
        auto &target = snapshot.commands[index];
        target.sequence = command.sequence;
        target.session_generation = command.session_generation;
        target.generation = command.generation;
        target.rule_id = command.rule_id;
        target.command_type = command.command_type;
        target.target_id = command.target_id;
        target.payload0 = command.payload0;
        target.payload1 = command.payload1;
        target.value0 = command.value0;
        target.value1 = command.value1;
    }

    std::memcpy(output, &snapshot, sizeof(snapshot));
    return 1;
}

#include "hud_logic_worker.h"
#include "hud_deadline_scheduler.h"
#include "realtime_event_core.h"

#include <array>
#include <atomic>
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <thread>

namespace {

void test_touch_state_and_event_cursor() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_touch(0, 7, 1, 1000, 120.0f, 240.0f, 1000, 500));
    assert(observe_touch(5, 11, 2, 1010, 820.0f, 260.0f, 1000, 500));
    assert(observe_touch(6, 7, 1, 1020, 120.0f, 240.0f, 1000, 500));

    const auto snapshot = read_input_snapshot();
    assert(snapshot.generation == 3);
    assert(snapshot.held_mask == 0x2u);
    assert(snapshot.last_down_mask == 0x2u);
    assert(snapshot.last_up_mask == 0x1u);
    assert(snapshot.total_count == 2);
    assert(snapshot.kps == 2.0f);
    assert(snapshot.latest_sequence == 3);

    std::array<InputEvent, 4> events{};
    const auto read = read_events(0, events.data(), events.size());
    assert(read.count == 3);
    assert(read.cursor == 3);
    assert(read.dropped_before_cursor == 0);
    assert(events[0].phase == InputPhase::Down);
    assert(events[0].slot == 0);
    assert(events[0].viewport_width == 1000);
    assert(events[0].viewport_height == 500);
    assert(std::fabs(events[0].x - 120.0f) < 0.001f);
    assert(events[1].phase == InputPhase::Down);
    assert(events[1].slot == 1);
    assert(events[2].phase == InputPhase::Up);
    assert(events[2].slot == 0);

    assert(refresh_kps(2'020'000'001LL));
    assert(read_input_snapshot().kps == 0.0f);
}

void test_event_cursor_can_open_at_current_tail() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_touch(0, 9, 1, 100, 100.0f, 100.0f, 1000, 500));
    assert(observe_touch(1, 9, 0, 101, 100.0f, 100.0f, 1000, 500));
    std::array<InputEvent, 4> events{};
    const auto opened = read_events(
        kOpenEventCursorAtTail,
        events.data(),
        events.size());
    assert(opened.count == 0u);
    assert(opened.dropped_before_cursor == 0u);
    assert(opened.cursor == 2u);

    assert(observe_touch(0, 10, 1, 102, 200.0f, 100.0f, 1000, 500));
    const auto live = read_events(opened.cursor, events.data(), events.size());
    assert(live.count == 1u);
    assert(live.cursor == 3u);
    assert(events[0].code == 10);
    assert(events[0].phase == InputPhase::Down);
}

void test_native_wait_wakes_on_raw_input_without_frame_polling() {
    using namespace starray::realtime;
    reset_for_tests();

    const auto before = read_input_snapshot();
    std::atomic<bool> woke{false};
    std::thread waiter([&] {
        wait_for_change(
            before.latest_sequence,
            before.generation,
            monotonic_now_ns() + 1'000'000'000LL);
        woke.store(true, std::memory_order_release);
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    assert(!woke.load(std::memory_order_acquire));
    assert(observe_touch(0, 9, 1, 100, 100.0f, 100.0f, 1000, 500));
    waiter.join();
    assert(woke.load(std::memory_order_acquire));
}

void test_session_boundary_resets_input_state_without_rewinding_ring() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_touch(0, 1, 1, 1000, 100.0f, 200.0f, 1000, 500));
    const uint64_t before_sequence = read_input_snapshot().latest_sequence;
    const uint32_t session = begin_session(1'500'000'000LL);
    const auto snapshot = read_input_snapshot();

    assert(session == 1);
    assert(snapshot.session_generation == 1);
    assert(snapshot.session_anchor_raw_ns == 1'500'000'000LL);
    assert(snapshot.held_mask == 0);
    assert(snapshot.keyboard_held_count == 0);
    assert(snapshot.total_count == 0);
    assert(snapshot.kps == 0.0f);
    assert(snapshot.latest_sequence == before_sequence + 1);

    std::array<InputEvent, 2> events{};
    const auto read = read_events(before_sequence, events.data(), events.size());
    assert(read.count == 1);
    assert(events[0].source == InputSource::Synthetic);
    assert(events[0].phase == InputPhase::Reset);
    assert(events[0].session_generation == 1);
}

void test_session_boundary_captures_external_device_snapshot() {
    using namespace starray::realtime;
    reset_for_tests();
    set_external_input_devices(
        kExternalInputDeviceKeyboard | kExternalInputDeviceController | (1u << 31));
    const auto devices = read_external_input_devices();
    assert(devices.abi_version == 1);
    assert(devices.flags ==
           (kExternalInputDeviceKeyboard | kExternalInputDeviceController));

    const auto before = read_input_snapshot();
    begin_session(1'600'000'000LL);
    std::array<InputEvent, 2> events{};
    const auto read = read_events(before.latest_sequence, events.data(), events.size());
    assert(read.count == 1);
    assert(events[0].phase == InputPhase::Reset);
    assert(events[0].flags == devices.flags);

    set_external_input_devices(kExternalInputDeviceMouse);
    assert(read_external_input_devices().flags == kExternalInputDeviceMouse);
    assert(events[0].flags == devices.flags);
}

void test_keyboard_metadata_and_repeat_suppression() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_key(0, 29, 30, 0x1, 4, 0, 1100, 0));
    assert(observe_key(0, 29, 30, 0x1, 4, 1, 1110, 0x80));

    auto snapshot = read_input_snapshot();
    assert(snapshot.generation == 1);
    assert(snapshot.keyboard_held_count == 1);
    assert(snapshot.total_count == 1);
    assert(snapshot.kps == 1.0f);
    assert(snapshot.latest_sequence == 2);

    std::array<InputEvent, 4> events{};
    auto read = read_events(0, events.data(), events.size());
    assert(read.count == 2);
    assert(events[0].source == InputSource::Keyboard);
    assert(events[0].phase == InputPhase::Down);
    assert(events[0].code == 29);
    assert(events[0].scan_code == 30);
    assert(events[0].meta_state == 0x1);
    assert(events[0].device_id == 4);
    assert(events[0].repeat_count == 0);
    assert(events[1].repeat_count == 1);
    assert(events[1].android_flags == 0x80);
    assert(events[1].flags != 0);

    LegacyInputSnapshot legacy{};
    assert(read_legacy_input_snapshot(0, legacy));
    assert(legacy.abi_version == 1u);
    assert(legacy.struct_size == sizeof(LegacyInputSnapshot));
    assert(legacy.keyboard_lifetime_down_count == 1u);
    assert(legacy.down_ordinals[97] == 1u);
    assert((legacy.held_words[1] & (1ULL << (97 - 64))) != 0u);
    const uint64_t legacy_generation = legacy.generation;
    assert(!read_legacy_input_snapshot(legacy_generation, legacy));

    assert(observe_key(1, 29, 30, 0x1, 4, 0, 1120, 0));
    snapshot = read_input_snapshot();
    assert(snapshot.generation == 2);
    assert(snapshot.keyboard_held_count == 0);
    assert(snapshot.total_count == 1);
    assert(snapshot.latest_sequence == 3);

    read = read_events(2, events.data(), events.size());
    assert(read.count == 1);
    assert(events[0].phase == InputPhase::Up);
    assert(read_legacy_input_snapshot(legacy_generation, legacy));
    assert(legacy.up_ordinals[97] == 1u);
    assert((legacy.held_words[1] & (1ULL << (97 - 64))) == 0u);
}

void test_count_journal_preserves_identity_and_touch_projection_totals() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_touch(0, 1, 1, 1000, 120.0f, 200.0f, 1000, 500));
    assert(observe_touch(1, 1, 1, 1001, 120.0f, 200.0f, 1000, 500));
    assert(observe_touch(0, 2, 1, 1002, 820.0f, 200.0f, 1000, 500));
    assert(observe_touch(1, 2, 1, 1003, 820.0f, 200.0f, 1000, 500));
    assert(observe_key(0, 29, 30, 0, 4, 0, 1004, 0));
    assert(observe_key(0, 29, 30, 0, 4, 1, 1005, 0));
    assert(observe_key(1, 29, 30, 0, 4, 0, 1006, 0));

    auto journal = read_count_journal_snapshot();
    assert(journal.lifetime_down_count == 3u);
    assert(journal.session_down_count == 3u);
    assert(journal.key_identity_count == 1u);
    assert(journal.key_identity_overflow_count == 0u);
    assert(journal.touch_projections[0].lane_count == 2u);
    assert(journal.touch_projections[0].session_down_counts[0] == 1u);
    assert(journal.touch_projections[0].session_down_counts[1] == 1u);
    assert(journal.touch_projections[4].lane_count == 10u);
    assert(journal.touch_projections[4].session_down_counts[1] == 1u);
    assert(journal.touch_projections[4].session_down_counts[8] == 1u);
    assert(journal.touch_projections[4].held_mask == 0u);

    const KeyCountJournalEntry *key = nullptr;
    for (const auto &candidate : journal.key_identities) {
        if (candidate.occupied != 0) {
            key = &candidate;
            break;
        }
    }
    assert(key != nullptr);
    assert(key->source == InputSource::Keyboard);
    assert(key->code == 29);
    assert(key->scan_code == 30);
    assert(key->device_id == 4);
    assert(key->lifetime_down_count == 1u);
    assert(key->session_down_count == 1u);

    const uint64_t lifetime_before_reset = journal.lifetime_down_count;
    begin_session(2'000'000'000LL);
    journal = read_count_journal_snapshot();
    assert(journal.lifetime_down_count == lifetime_before_reset);
    assert(journal.session_down_count == 0u);
    assert(journal.touch_projections[0].lifetime_down_counts[0] == 1u);
    assert(journal.touch_projections[0].session_down_counts[0] == 0u);
    assert(journal.key_identity_count == 1u);
    for (const auto &candidate : journal.key_identities) {
        if (candidate.occupied != 0)
            assert(candidate.session_down_count == 0u);
    }
}

void test_count_journal_survives_event_ring_overwrite() {
    using namespace starray::realtime;
    reset_for_tests();

    constexpr int kPressCount =
        static_cast<int>(kRawInputEventJournalCapacity / 2) + 188;
    for (int index = 0; index < kPressCount; ++index) {
        const int64_t event_ms = 1000 + index * 2;
        const float x = index % 2 == 0 ? 100.0f : 900.0f;
        assert(observe_touch(0, 7, 1, event_ms, x, 200.0f, 1000, 500));
        assert(observe_touch(1, 7, 1, event_ms + 1, x, 200.0f, 1000, 500));
    }

    std::array<InputEvent, 8> events{};
    const auto read = read_events(0, events.data(), events.size());
    assert(read.dropped_before_cursor > 0u);

    const auto journal = read_count_journal_snapshot();
    assert(journal.lifetime_down_count == kPressCount);
    assert(journal.session_down_count == kPressCount);
    assert(journal.touch_projections[0].session_down_counts[0] ==
           static_cast<uint64_t>(kPressCount / 2));
    assert(journal.touch_projections[0].session_down_counts[1] ==
           static_cast<uint64_t>(kPressCount / 2));
    assert(journal.touch_projections[4].session_down_counts[1] ==
           static_cast<uint64_t>(kPressCount / 2));
    assert(journal.touch_projections[4].session_down_counts[9] ==
           static_cast<uint64_t>(kPressCount / 2));
    assert(journal.touch_projections[4].held_mask == 0u);
}

void test_touch_lane_mapping_can_follow_distinct_contacts() {
    using namespace starray::realtime;
    reset_for_tests();
    assert(touch_contact_reuse_delay_ms() == 80);
    assert(!set_touch_contact_reuse_delay_ms(-1));
    assert(!set_touch_contact_reuse_delay_ms(501));
    assert(touch_contact_reuse_delay_ms() == 80);
    assert(set_touch_lane_mapping_mode(TouchLaneMappingMode::TouchContacts));
    assert(touch_lane_mapping_mode() == TouchLaneMappingMode::TouchContacts);

    assert(observe_touch(0, 11, 1, 1000, 100.0f, 200.0f, 1000, 500));
    assert(observe_touch(5, 12, 2, 1001, 100.0f, 200.0f, 1000, 500));
    auto journal = read_count_journal_snapshot();
    assert(journal.touch_projections[0].held_mask == 0b11u);
    assert(journal.touch_projections[0].session_down_counts[0] == 1u);
    assert(journal.touch_projections[0].session_down_counts[1] == 1u);
    assert(journal.touch_projections[4].session_down_counts[0] == 1u);
    assert(journal.touch_projections[4].session_down_counts[1] == 1u);

    assert(observe_touch(6, 12, 2, 1002, 100.0f, 200.0f, 1000, 500));
    assert(observe_touch(1, 11, 1, 1003, 100.0f, 200.0f, 1000, 500));
    journal = read_count_journal_snapshot();
    assert(journal.touch_projections[0].held_mask == 0u);

    assert(set_touch_lane_mapping_mode(TouchLaneMappingMode::ScreenRegions));
    journal = read_count_journal_snapshot();
    assert(journal.touch_projections[0].session_down_counts[0] == 0u);
    assert(journal.touch_projections[0].session_down_counts[1] == 0u);
}

void test_touch_contact_reuse_delay_separates_rapid_sequential_taps() {
    using namespace starray::realtime;
    reset_for_tests();
    assert(set_touch_lane_mapping_mode(TouchLaneMappingMode::TouchContacts));
    assert(set_touch_contact_reuse_delay_ms(80));
    assert(touch_contact_reuse_delay_ms() == 80);

    assert(observe_touch(0, 21, 1, 1'000,
                         100.0f, 200.0f, 1000, 500));
    assert(observe_touch(1, 21, 1, 1'010,
                         100.0f, 200.0f, 1000, 500));
    assert(observe_touch(0, 22, 1, 1'030,
                         100.0f, 200.0f, 1000, 500));
    assert(observe_touch(1, 22, 1, 1'040,
                         100.0f, 200.0f, 1000, 500));

    auto journal = read_count_journal_snapshot();
    assert(journal.touch_projections[1].session_down_counts[0] == 1u);
    assert(journal.touch_projections[1].session_down_counts[1] == 1u);

    assert(observe_touch(0, 23, 1, 1'200,
                         100.0f, 200.0f, 1000, 500));
    assert(observe_touch(1, 23, 1, 1'210,
                         100.0f, 200.0f, 1000, 500));
    journal = read_count_journal_snapshot();
    assert(journal.touch_projections[1].session_down_counts[0] == 2u);
    assert(journal.touch_projections[1].session_down_counts[1] == 1u);
}

void test_input_producer_switch_cancels_held_and_rejects_stale_source() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(active_producer() == InputProducer::OfficialActivity);
    assert(observe_touch(0, 7, 1, 1000, 100.0f, 200.0f, 1000, 500));
    const auto before_switch = read_input_snapshot();
    assert(before_switch.held_mask == 1u);
    assert(before_switch.total_count == 1u);

    const uint32_t async_epoch = set_active_producer(
        InputProducer::AsyncInput,
        1'100'000'000LL);
    auto snapshot = read_input_snapshot();
    assert(snapshot.active_producer == InputProducer::AsyncInput);
    assert(snapshot.producer_epoch == async_epoch);
    assert(snapshot.held_mask == 0u);
    assert(snapshot.total_count == 1u);
    assert(!observe_touch(0, 8, 1, 1110, 200.0f, 200.0f, 1000, 500));
    assert(observe_touch_raw(
        InputProducer::AsyncInput,
        0,
        8,
        1,
        1'110'000'000LL,
        200.0f,
        200.0f,
        1000,
        500,
        0x00001002,
        3,
        0));

    snapshot = read_input_snapshot();
    assert(snapshot.total_count == 2u);
    assert(snapshot.held_mask == 1u);

    std::array<InputEvent, 8> events{};
    const auto read = read_events(before_switch.latest_sequence, events.data(), events.size());
    assert(read.count == 3u);
    assert(events[0].phase == InputPhase::Cancel);
    assert(events[0].producer == InputProducer::OfficialActivity);
    assert(events[1].phase == InputPhase::ProducerChanged);
    assert(events[1].producer == InputProducer::AsyncInput);
    assert(events[2].phase == InputPhase::Down);
    assert(events[2].producer == InputProducer::AsyncInput);
    assert(events[2].producer_epoch == async_epoch);
    assert(events[2].device_id == 3);

    const uint64_t before_official = snapshot.latest_sequence;
    set_active_producer(InputProducer::OfficialActivity, 1'120'000'000LL);
    snapshot = read_input_snapshot();
    assert(snapshot.active_producer == InputProducer::OfficialActivity);
    assert(snapshot.held_mask == 0u);
    assert(snapshot.total_count == 2u);
    assert(snapshot.latest_sequence > before_official);
}

void test_gameplay_accepted_stream_is_independent_from_physical_input() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_touch(0, 7, 1, 1000, 100.0f, 200.0f, 1000, 500));
    const auto physical_before = read_input_snapshot();
    assert(observe_gameplay_accepted(false, 0, 1'010'000'000LL));
    assert(observe_gameplay_accepted(false, 1, 1'020'000'000LL));
    assert(observe_gameplay_accepted(true, 0, 1'030'000'000LL));
    assert(observe_gameplay_accepted(false, 0, 1'035'000'000LL, true));
    assert(!observe_gameplay_accepted(false, 2, 1'040'000'000LL));

    const auto physical_after = read_input_snapshot();
    assert(physical_after.generation == physical_before.generation);
    assert(physical_after.latest_sequence == physical_before.latest_sequence);
    assert(physical_after.held_mask == physical_before.held_mask);
    assert(physical_after.total_count == physical_before.total_count);
    assert(physical_after.kps == physical_before.kps);

    const auto accepted = read_gameplay_accepted_snapshot();
    assert(accepted.total_count == 4u);
    assert(accepted.physical_count == 2u);
    assert(accepted.synthetic_count == 2u);
    assert(accepted.latest_sequence == 4u);

    std::array<GameplayAcceptedEvent, 4> events{};
    const auto read = read_gameplay_accepted_events(0, events.data(), events.size());
    assert(read.count == 4u);
    assert(events[0].source == InputSource::GameAction);
    assert(events[0].phase == InputPhase::Down);
    assert(!events[0].is_auto);
    assert(events[1].phase == InputPhase::Up);
    assert(events[2].source == InputSource::Synthetic);
    assert(events[2].is_auto);
    assert(events[3].source == InputSource::Synthetic);
    assert(!events[3].is_auto);
    assert(events[3].is_test_macro);

    set_active_producer(InputProducer::AsyncInput, 1'050'000'000LL);
    assert(observe_gameplay_accepted(false, 0, 1'060'000'000LL));
    const auto producer_read = read_gameplay_accepted_events(
        read.cursor,
        events.data(),
        events.size());
    assert(producer_read.count == 1u);
    assert(events[0].producer == InputProducer::AsyncInput);
    assert(events[0].producer_epoch == read_input_snapshot().producer_epoch);
}

void test_gameplay_accepted_session_reset_preserves_cursor() {
    using namespace starray::realtime;
    reset_for_tests();

    assert(observe_gameplay_accepted(false, 0, 100));
    const uint64_t before_sequence = read_gameplay_accepted_snapshot().latest_sequence;
    const uint32_t session = begin_session(200);
    auto snapshot = read_gameplay_accepted_snapshot();
    assert(snapshot.session_generation == session);
    assert(snapshot.total_count == 0u);
    assert(snapshot.physical_count == 0u);
    assert(snapshot.synthetic_count == 0u);
    assert(snapshot.latest_sequence == before_sequence);

    assert(observe_gameplay_accepted(true, 0, 300));
    snapshot = read_gameplay_accepted_snapshot();
    assert(snapshot.latest_sequence == before_sequence + 1);
    assert(snapshot.total_count == 1u);
    assert(snapshot.synthetic_count == 1u);
}

void test_clock_anchor_completed_history() {
    using namespace starray::hud_logic;

    ClockAnchorSnapshot source{};
    source.session_generation = 7;
    source.valid_mask = ClockAnchorUnityScaled |
        ClockAnchorSongPosition |
        ClockAnchorFrameCount;
    source.frame_count = 321;
    source.unity_time_scale = 0.75f;
    source.unity_scaled_seconds = 12.5;
    source.song_position_seconds = 9.25;
    source.monotonic_raw_ns = 8'000'000'000LL;
    publish_clock_anchor(source);

    ClockAnchorSnapshot completed{};
    assert(read_latest_clock_anchor(completed));
    assert(completed.available == 1);
    assert(completed.publication_generation > 0);
    assert(completed.session_generation == 7);
    assert(completed.frame_count == 321);
    assert(std::fabs(completed.unity_time_scale - 0.75f) < 0.001f);
    assert(std::fabs(completed.unity_scaled_seconds - 12.5) < 0.001);
    assert(std::fabs(completed.song_position_seconds - 9.25) < 0.001);
    assert(completed.monotonic_raw_ns == 8'000'000'000LL);
}

void test_hud_worker_catches_up_without_blocking_reader() {
    using namespace starray;
    realtime::reset_for_tests();
    hud_logic::ensure_started();

    const int64_t now_ns = realtime::monotonic_now_ns();
    const int64_t now_ms = now_ns / 1'000'000LL;
    const uint32_t session = realtime::begin_session(now_ns);
    assert(realtime::observe_touch(0, 3, 1, now_ms, 400.0f, 300.0f, 1000, 500));
    assert(realtime::observe_touch(5, 4, 2, now_ms + 1, 420.0f, 300.0f, 1000, 500));
    assert(realtime::observe_touch(6, 3, 1, now_ms + 2, 400.0f, 300.0f, 1000, 500));

    hud_logic::CompletedInputSnapshot completed{};
    bool caught_up = false;
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto producer = realtime::read_input_snapshot();
        if (hud_logic::read_latest_input_snapshot(completed) &&
            completed.source_generation >= producer.generation &&
            completed.source_sequence >= producer.latest_sequence) {
            caught_up = true;
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }

    assert(caught_up);
    assert(completed.session_generation == session);
    assert(completed.session_anchor_raw_ns == now_ns);
    assert(completed.held_mask == 0x2u);
    assert(completed.total_count == 2);
    assert(completed.touch_lane_count == 10);
    assert(completed.touch_lane_held_mask == (1u << 4));
    assert(completed.touch_lane_held_counts[4] == 1);
    assert(completed.touch_lane_total_counts[4] == 2);
    assert(completed.touch_lane_last_down_mask == (1u << 4));
    assert(completed.touch_lane_last_up_mask == (1u << 4));
    assert(completed.consumer_dropped_event_count == 0);

    hud_logic::TouchLaneProjectionSnapshot lanes2{};
    hud_logic::TouchLaneProjectionSnapshot lanes4{};
    hud_logic::TouchLaneProjectionSnapshot lanes10{};
    assert(hud_logic::select_touch_lane_projection(completed, 2, lanes2));
    assert(hud_logic::select_touch_lane_projection(completed, 4, lanes4));
    assert(hud_logic::select_touch_lane_projection(completed, 10, lanes10));
    assert(!hud_logic::select_touch_lane_projection(completed, 3, lanes10));
    assert(lanes2.held_mask == (1u << 0));
    assert(lanes2.held_counts[0] == 1);
    assert(lanes2.total_counts[0] == 2);
    assert(lanes4.held_mask == (1u << 1));
    assert(lanes4.held_counts[1] == 1);
    assert(lanes4.total_counts[1] == 2);
    assert(lanes10.held_mask == (1u << 4));
}

void test_bounded_multi_clock_deadline_scheduler() {
    using namespace starray::hud_logic;

    DeadlineScheduler scheduler;
    SchedulerClock clock{};
    clock.realtime_now_ns = 100;
    clock.anchor.available = 1;
    clock.anchor.valid_mask = ClockAnchorUnityScaled | ClockAnchorSongPosition;
    clock.anchor.unity_scaled_seconds = 4.0;
    clock.anchor.song_position_seconds = 9.0;
    clock.anchor.unity_time_scale = 0.5f;
    clock.anchor.monotonic_raw_ns = 100;

    assert(scheduler.schedule(ScheduledPresentationTask{
        .session_generation = 7,
        .domain = ClockDomain::Realtime,
        .deadline_ns = 101,
        .command_type = 1,
    }));
    assert(scheduler.schedule(ScheduledPresentationTask{
        .session_generation = 7,
        .domain = ClockDomain::UnityScaled,
        .deadline_ns = 5'000'000'000LL,
        .command_type = 2,
    }));
    assert(scheduler.schedule(ScheduledPresentationTask{
        .session_generation = 6,
        .domain = ClockDomain::Song,
        .deadline_ns = 9'000'000'000LL,
        .command_type = 3,
    }));

    std::array<ScheduledPresentationTask, kMaxDuePresentationTasks> due{};
    uint64_t stale = 0;
    assert(scheduler.pop_due(clock, 7, due, stale) == 0);
    assert(stale == 1);
    assert(scheduler.size() == 2);

    clock.realtime_now_ns = 101;
    clock.anchor.unity_scaled_seconds = 5.0;
    assert(scheduler.pop_due(clock, 7, due, stale) == 2);
    assert(due[0].command_type == 1);
    assert(due[1].command_type == 2);

    scheduler.reset();
    clock.realtime_now_ns = 2'000'000'000LL;
    clock.anchor.monotonic_raw_ns = 1'000'000'000LL;
    clock.anchor.unity_scaled_seconds = 4.0;
    clock.anchor.unity_time_scale = 0.5f;
    assert(scheduler.schedule(ScheduledPresentationTask{
        .session_generation = 7,
        .domain = ClockDomain::UnityScaled,
        .deadline_ns = 5'000'000'000LL,
        .command_type = 4,
        .flags = SchedulerAllowAnchorExtrapolation,
    }));
    assert(scheduler.next_wake_raw_ns(clock, 7) == 3'000'000'000LL);
    assert(scheduler.pop_due(clock, 7, due, stale) == 0);
    clock.realtime_now_ns = 3'000'000'000LL;
    assert(scheduler.pop_due(clock, 7, due, stale) == 1);
    assert(due[0].command_type == 4);

    scheduler.reset();
    for (size_t index = 0; index < kMaxScheduledPresentationTasksPerDomain; ++index)
        assert(scheduler.schedule(ScheduledPresentationTask{
            .domain = ClockDomain::Realtime,
            .deadline_ns = static_cast<int64_t>(index + 1),
        }));
    assert(!scheduler.schedule(ScheduledPresentationTask{
        .domain = ClockDomain::Realtime,
        .deadline_ns = 1000,
    }));
    assert(scheduler.dropped_overflow() == 1);
}

}  // namespace

int main() {
    test_touch_state_and_event_cursor();
    test_event_cursor_can_open_at_current_tail();
    test_native_wait_wakes_on_raw_input_without_frame_polling();
    test_session_boundary_resets_input_state_without_rewinding_ring();
    test_session_boundary_captures_external_device_snapshot();
    test_keyboard_metadata_and_repeat_suppression();
    test_count_journal_preserves_identity_and_touch_projection_totals();
    test_count_journal_survives_event_ring_overwrite();
    test_touch_lane_mapping_can_follow_distinct_contacts();
    test_touch_contact_reuse_delay_separates_rapid_sequential_taps();
    test_input_producer_switch_cancels_held_and_rejects_stale_source();
    test_gameplay_accepted_stream_is_independent_from_physical_input();
    test_gameplay_accepted_session_reset_preserves_cursor();
    test_clock_anchor_completed_history();
    test_hud_worker_catches_up_without_blocking_reader();
    test_bounded_multi_clock_deadline_scheduler();
    return 0;
}

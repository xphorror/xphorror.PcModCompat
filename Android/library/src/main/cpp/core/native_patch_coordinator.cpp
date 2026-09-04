#include "native_patch_coordinator.h"

#include <algorithm>
#include <limits>
#include <sstream>

namespace starray::native_patch {
namespace {

constexpr size_t kMaximumReservations = 4096;

struct Reservation {
    uint64_t id = 0;
    Kind kind = Kind::Hook;
    std::string domain;
    std::string owner;
    uint64_t generation = 0;
    uintptr_t begin = 0;
    uintptr_t end = 0;
    bool committed = false;
};

std::mutex g_mutex;
std::vector<Reservation> g_reservations;
uint64_t g_next_id = 0;

bool ranges_overlap(
    uintptr_t left_begin,
    uintptr_t left_end,
    uintptr_t right_begin,
    uintptr_t right_end) {
    return left_begin < right_end && right_begin < left_end;
}

auto find_reservation(uint64_t id) {
    return std::find_if(
        g_reservations.begin(),
        g_reservations.end(),
        [id](const Reservation &reservation) {
            return reservation.id == id;
        });
}

void erase_pending_id(std::vector<uint64_t> &pending_ids, uint64_t id) {
    pending_ids.erase(
        std::remove(pending_ids.begin(), pending_ids.end(), id),
        pending_ids.end());
}

} // namespace

Transaction::Transaction()
    : lock_(g_mutex) {
    pending_ids_.reserve(4);
}

Transaction::~Transaction() {
    for (uint64_t id : pending_ids_) {
        const auto reservation = find_reservation(id);
        if (reservation != g_reservations.end() && !reservation->committed)
            g_reservations.erase(reservation);
    }
}

ReserveResult Transaction::Reserve(
    Kind kind,
    const char *domain,
    const char *owner,
    uint64_t generation,
    void *address,
    size_t size,
    ReservationToken &token,
    std::string &error) {
    token = {};
    error.clear();
    const uintptr_t begin = reinterpret_cast<uintptr_t>(address);
    if (domain == nullptr || domain[0] == '\0' || address == nullptr ||
        size == 0 || begin > std::numeric_limits<uintptr_t>::max() - size) {
        error = "invalid native patch reservation";
        return ReserveResult::Invalid;
    }
    const uintptr_t end = begin + size;

    for (const auto &existing : g_reservations) {
        if (!ranges_overlap(begin, end, existing.begin, existing.end))
            continue;
        if (existing.begin == begin && existing.end == end &&
            existing.kind == kind && existing.domain == domain) {
            token.id = existing.id;
            token.acquired = false;
            return ReserveResult::Reused;
        }

        std::ostringstream message;
        message << "native patch range conflict requested="
                << KindName(kind) << '/' << domain
                << " owner=" << (owner != nullptr ? owner : "<null>")
                << " generation=" << generation
                << " range=0x" << std::hex << begin << "-0x" << end
                << " existing=" << KindName(existing.kind) << '/'
                << existing.domain
                << " owner=" << existing.owner
                << " generation=" << std::dec << existing.generation
                << " range=0x" << std::hex << existing.begin
                << "-0x" << existing.end;
        error = message.str();
        return ReserveResult::Conflict;
    }

    if (g_reservations.size() >= kMaximumReservations ||
        g_next_id == std::numeric_limits<uint64_t>::max()) {
        error = "native patch reservation registry exhausted";
        return ReserveResult::Exhausted;
    }

    Reservation reservation;
    reservation.id = ++g_next_id;
    reservation.kind = kind;
    reservation.domain = domain;
    reservation.owner = owner != nullptr ? owner : "<null>";
    reservation.generation = generation;
    reservation.begin = begin;
    reservation.end = end;
    pending_ids_.push_back(reservation.id);
    g_reservations.push_back(std::move(reservation));
    token.id = g_next_id;
    token.acquired = true;
    return ReserveResult::Acquired;
}

void Transaction::Commit(ReservationToken &token) {
    if (!token.acquired || token.id == 0)
        return;
    const auto reservation = find_reservation(token.id);
    if (reservation != g_reservations.end())
        reservation->committed = true;
    erase_pending_id(pending_ids_, token.id);
    token.acquired = false;
}

void Transaction::Rollback(ReservationToken &token) {
    if (!token.acquired || token.id == 0)
        return;
    const auto reservation = find_reservation(token.id);
    if (reservation != g_reservations.end() && !reservation->committed)
        g_reservations.erase(reservation);
    erase_pending_id(pending_ids_, token.id);
    token = {};
}

const char *KindName(Kind kind) {
    switch (kind) {
        case Kind::Hook:
            return "hook";
        case Kind::Instrument:
            return "instrument";
        case Kind::CodePatch:
            return "code-patch";
        default:
            return "unknown";
    }
}

} // namespace starray::native_patch

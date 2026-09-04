#ifndef STARRAY_NATIVE_PATCH_COORDINATOR_H
#define STARRAY_NATIVE_PATCH_COORDINATOR_H

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#ifndef STARRAY_NATIVE_INTERNAL
#if defined(__GNUC__) && !defined(_WIN32)
#define STARRAY_NATIVE_INTERNAL __attribute__((visibility("hidden")))
#else
#define STARRAY_NATIVE_INTERNAL
#endif
#endif

namespace starray::native_patch {

inline constexpr size_t kConservativeDobbyArm64PatchSize = 16;

enum class Kind : uint32_t {
    Hook = 1,
    Instrument = 2,
    CodePatch = 3,
};

enum class ReserveResult : uint32_t {
    Acquired = 1,
    Reused = 2,
    Conflict = 3,
    Invalid = 4,
    Exhausted = 5,
};

struct ReservationToken {
    uint64_t id = 0;
    bool acquired = false;
};

class STARRAY_NATIVE_INTERNAL Transaction final {
public:
    Transaction();
    ~Transaction();

    Transaction(const Transaction &) = delete;
    Transaction &operator=(const Transaction &) = delete;

    ReserveResult Reserve(
        Kind kind,
        const char *domain,
        const char *owner,
        uint64_t generation,
        void *address,
        size_t size,
        ReservationToken &token,
        std::string &error);
    void Commit(ReservationToken &token);
    void Rollback(ReservationToken &token);

private:
    std::unique_lock<std::mutex> lock_;
    std::vector<uint64_t> pending_ids_;
};

STARRAY_NATIVE_INTERNAL const char *KindName(Kind kind);

} // namespace starray::native_patch

#endif

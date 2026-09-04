#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/types.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <unistd.h>

static int range_is_readable(const void *address, size_t size) {
    if (address == NULL || size == 0) {
        return 0;
    }

    const uintptr_t begin = (uintptr_t)address;
    if (begin > UINTPTR_MAX - size) {
        return 0;
    }
    const uintptr_t end = begin + size;

    FILE *maps = fopen("/proc/self/maps", "r");
    if (maps == NULL) {
        return 0;
    }

    char line[512];
    int readable = 0;
    while (fgets(line, sizeof(line), maps) != NULL) {
        unsigned long long map_begin = 0;
        unsigned long long map_end = 0;
        char permissions[5] = {0};
        if (sscanf(line, "%llx-%llx %4s", &map_begin, &map_end, permissions) != 3) {
            continue;
        }
        if (permissions[0] == 'r' &&
            begin >= (uintptr_t)map_begin &&
            end <= (uintptr_t)map_end) {
            readable = 1;
            break;
        }
    }

    fclose(maps);
    return readable;
}

__attribute__((visibility("default")))
int modmanager_try_read_process_memory(
    const void *address,
    void *output,
    size_t size) {
    if (address == NULL || output == NULL || size == 0) {
        return 0;
    }

    struct iovec local = {output, size};
    struct iovec remote = {(void *)address, size};
    const ssize_t copied = (ssize_t)syscall(
        __NR_process_vm_readv,
        getpid(),
        &local,
        1,
        &remote,
        1,
        0);
    if (copied == (ssize_t)size) {
        return 1;
    }

    if (!range_is_readable(address, size)) {
        return 0;
    }
    memcpy(output, address, size);
    return 1;
}

#ifndef ADOFAI_APP_FILES_H
#define ADOFAI_APP_FILES_H

#include <errno.h>
#include <limits.h>
#include <pthread.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

#ifndef PATH_MAX
#define PATH_MAX 4096
#endif

typedef struct AdoAppFilesState {
    pthread_mutex_t mutex;
    pthread_cond_t ready;
    int status;
    char path[PATH_MAX];
} AdoAppFilesState;

#define ADO_APP_FILES_STATE_INITIALIZER \
    { PTHREAD_MUTEX_INITIALIZER, PTHREAD_COND_INITIALIZER, 0, {0} }

static inline int ado_app_files_has_android_root(const char *path) {
    return path != NULL &&
        (strncmp(path, "/data/user/", 11) == 0 ||
         strncmp(path, "/data/data/", 11) == 0);
}

static inline int ado_app_files_has_files_leaf(const char *path) {
    if (path == NULL) {
        return 0;
    }
    size_t len = strlen(path);
    return len > 6 && strcmp(path + len - 6, "/files") == 0;
}

static inline int ado_app_files_validate(const char *path, char out[PATH_MAX]) {
    if (path == NULL || path[0] != '/' || strchr(path, '\n') != NULL ||
        strchr(path, '\r') != NULL) {
        return 0;
    }

    char resolved[PATH_MAX];
    if (realpath(path, resolved) == NULL ||
        !ado_app_files_has_android_root(resolved) ||
        !ado_app_files_has_files_leaf(resolved)) {
        return 0;
    }

    struct stat st;
    if (lstat(resolved, &st) != 0 || !S_ISDIR(st.st_mode) ||
        st.st_uid != geteuid()) {
        return 0;
    }

    size_t len = strlen(resolved);
    if (len + 1 > PATH_MAX) {
        return 0;
    }
    memcpy(out, resolved, len + 1);
    return 1;
}

static inline int ado_app_files_configure(AdoAppFilesState *state, const char *path) {
    if (state == NULL) {
        return 0;
    }

    char validated[PATH_MAX];
    int valid = ado_app_files_validate(path, validated);

    pthread_mutex_lock(&state->mutex);
    if (state->status > 0) {
        int same = valid && strcmp(state->path, validated) == 0;
        pthread_mutex_unlock(&state->mutex);
        return same;
    }
    if (state->status < 0) {
        pthread_mutex_unlock(&state->mutex);
        return 0;
    }
    if (!valid) {
        state->status = -1;
        pthread_cond_broadcast(&state->ready);
        pthread_mutex_unlock(&state->mutex);
        return 0;
    }

    memcpy(state->path, validated, strlen(validated) + 1);
    state->status = 1;
    pthread_cond_broadcast(&state->ready);
    pthread_mutex_unlock(&state->mutex);
    return 1;
}

static inline int ado_app_files_wait(AdoAppFilesState *state, char *out, size_t out_cap) {
    if (state == NULL || out == NULL || out_cap == 0) {
        return 0;
    }
    pthread_mutex_lock(&state->mutex);
    while (state->status == 0) {
        pthread_cond_wait(&state->ready, &state->mutex);
    }
    int ok = state->status > 0 && strlen(state->path) + 1 <= out_cap;
    if (ok) {
        memcpy(out, state->path, strlen(state->path) + 1);
    }
    pthread_mutex_unlock(&state->mutex);
    return ok;
}

static inline int ado_app_files_get(AdoAppFilesState *state, char *out, size_t out_cap) {
    if (state == NULL || out == NULL || out_cap == 0) {
        return 0;
    }
    pthread_mutex_lock(&state->mutex);
    int ok = state->status > 0 && strlen(state->path) + 1 <= out_cap;
    if (ok) {
        memcpy(out, state->path, strlen(state->path) + 1);
    }
    pthread_mutex_unlock(&state->mutex);
    return ok;
}

static inline int ado_app_files_join(char *out, size_t out_cap,
                                     const char *files_dir, const char *leaf) {
    if (out == NULL || out_cap == 0 || files_dir == NULL || leaf == NULL ||
        leaf[0] == '\0' || strchr(leaf, '/') != NULL) {
        return 0;
    }
    int n = snprintf(out, out_cap, "%s/%s", files_dir, leaf);
    return n > 0 && (size_t)n < out_cap;
}

#endif

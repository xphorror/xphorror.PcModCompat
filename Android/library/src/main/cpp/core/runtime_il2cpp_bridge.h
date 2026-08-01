#ifndef STARRAY_RUNTIME_IL2CPP_BRIDGE_H
#define STARRAY_RUNTIME_IL2CPP_BRIDGE_H

#ifdef __cplusplus
extern "C" {
#endif

int modmanager_runtime_configure_app_files_dir(const char *path);
int modmanager_runtime_enabled(void);
void *modmanager_libil2cpp_handle(void);

#ifdef __cplusplus
}
#endif

#endif

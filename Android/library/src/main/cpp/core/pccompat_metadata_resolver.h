#pragma once

#include <string>
#include <vector>

namespace starray::pccompat_metadata {

struct MethodIdentity {
    std::string assembly_name;
    std::string namespace_name;
    std::string type_name;
    std::string method_name;
    std::string return_type;
    std::vector<std::string> parameter_types;
    bool is_static = false;
};

struct ResolvedMethod {
    void *method_info = nullptr;
    void *function = nullptr;
};

struct ResolvedClass {
    void *klass = nullptr;
};

bool resolve_method(const MethodIdentity &identity,
                    ResolvedMethod &method,
                    std::string &error);

// UnityMain-only runtime helpers.  They share the coordinator's metadata
// cache; callers never provide RVA/VA values or invoke Unity from worker
// threads.  The helpers intentionally expose only the bounded operations
// needed by the presentation sink.
bool resolve_class(const std::string &assembly_name,
                   const std::string &namespace_name,
                   const std::string &type_name,
                   ResolvedClass &klass,
                   std::string &error);

bool runtime_invoke(const ResolvedMethod &method,
                    void *instance,
                    void **args,
                    void **result,
                    std::string &error);

bool allocate_object(const ResolvedClass &klass,
                     void **object,
                     std::string &error);

bool allocate_reference_array(const ResolvedClass &element_class,
                              const std::vector<void *> &elements,
                              void **array,
                              std::string &error);

bool get_type_object(const ResolvedClass &klass,
                     void **type_object,
                     std::string &error);

bool new_managed_string(const std::string &value,
                        void **managed_string,
                        std::string &error);

// GCHandle values are opaque 64-bit tagged pointers on Unity 6 (Il2CppGCHandle
// == void*), not 32-bit indices; keep them pointer-sized end to end.
bool create_gc_handle(void *object,
                      void *&handle,
                      std::string &error);
void free_gc_handle(void *handle);

bool resolve_field_offset(const ResolvedClass &klass,
                          const std::string &field_name,
                          int32_t &offset,
                          std::string &error);

}  // namespace starray::pccompat_metadata

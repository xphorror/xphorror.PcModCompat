#include "unity_presentation_objects.h"

#include "pccompat_metadata_resolver.h"
#include "pccompat_presentation_abi.h"

#include <android/log.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>

#define LOG_TAG "StArray.PresentationObjects"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace starray::unity_presentation_objects {
namespace {

using starray::pccompat_metadata::MethodIdentity;
using starray::pccompat_metadata::ResolvedClass;
using starray::pccompat_metadata::ResolvedMethod;

struct ClassSlot {
    ResolvedClass value{};
};

struct MethodSlot {
    ResolvedMethod value{};
};

struct Vec2 {
    float x = 0.0f;
    float y = 0.0f;
};

struct Color {
    float r = 0.0f;
    float g = 0.0f;
    float b = 0.0f;
    float a = 1.0f;
};

static_assert(sizeof(Vec2) == 8);
static_assert(sizeof(Color) == 16);

float payload_float(int64_t payload) {
    const uint32_t word = static_cast<uint32_t>(static_cast<uint64_t>(payload));
    float value = 0.0f;
    std::memcpy(&value, &word, sizeof(value));
    return value;
}

bool payload_bool(int64_t payload) {
    return payload != 0;
}

int32_t payload_int(int64_t payload) {
    return static_cast<int32_t>(payload);
}

using ResourceResolverCallback = int (*)(
    const char *mod_id,
    const char *feature_group_id,
    const char *asset_name,
    const char *expected_type,
    void **asset);

std::atomic<ResourceResolverCallback> g_resource_resolver{nullptr};

class UnityApi final {
public:
    bool ensure_core(std::string &error) {
        if (core_ready_)
            return true;

        if (!resolve_class(core_module_, "UnityEngine.CoreModule", "UnityEngine", "Object", error) ||
            !resolve_class(game_object_class_, "UnityEngine.CoreModule", "UnityEngine", "GameObject", error) ||
            !resolve_class(transform_class_, "UnityEngine.CoreModule", "UnityEngine", "Transform", error) ||
            !resolve_class(rect_transform_class_, "UnityEngine.CoreModule", "UnityEngine", "RectTransform", error) ||
            !resolve_class(system_type_class_, "mscorlib", "System", "Type", error)) {
            return false;
        }

        if (!resolve_method(
                game_object_ctor_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "GameObject",
                    .method_name = ".ctor",
                    .return_type = "System.Void",
                    .parameter_types = {"System.String", "System.Type[]"},
                    .is_static = false,
                },
                error)) {
            return false;
        }

        if (!resolve_method(
                add_component_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "GameObject",
                    .method_name = "AddComponent",
                    .return_type = "UnityEngine.Component",
                    .parameter_types = {"System.Type"},
                    .is_static = false,
                },
                error)) {
            return false;
        }

        if (!resolve_method(
                get_transform_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "GameObject",
                    .method_name = "get_transform",
                    .return_type = "UnityEngine.Transform",
                    .parameter_types = {},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        if (!resolve_method(
                set_parent_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "Transform",
                    .method_name = "SetParent",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Transform", "System.Boolean"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        if (!resolve_method(
                dont_destroy_on_load_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "Object",
                    .method_name = "DontDestroyOnLoad",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Object"},
                    .is_static = true,
                },
                error)) {
            return false;
        }
        if (!resolve_method(
                destroy_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "Object",
                    .method_name = "Destroy",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Object"},
                    .is_static = true,
                },
                error)) {
            return false;
        }

        std::string cached_ptr_error;
        if (!resolve_field_offset(
                core_module_,
                "m_CachedPtr",
                cached_ptr_offset_,
                cached_ptr_error)) {
            cached_ptr_offset_ = -1;
            LOGI("Unity object fake-null field unavailable: %s", cached_ptr_error.c_str());
        }

        core_ready_ = true;
        return true;
    }

    bool ensure_canvas(std::string &error) {
        if (!ensure_core(error))
            return false;
        if (canvas_ready_)
            return true;
        if (!resolve_class(canvas_class_, "UnityEngine.UIModule", "UnityEngine", "Canvas", error) ||
            !resolve_method(
                canvas_render_mode_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UIModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "Canvas",
                    .method_name = "set_renderMode",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.RenderMode"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                canvas_sorting_order_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UIModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "Canvas",
                    .method_name = "set_sortingOrder",
                    .return_type = "System.Void",
                    .parameter_types = {"System.Int32"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        canvas_ready_ = true;
        return true;
    }

    bool ensure_scaler(std::string &error) {
        if (!ensure_core(error))
            return false;
        if (scaler_ready_)
            return true;
        if (!resolve_class(
                canvas_scaler_class_,
                "UnityEngine.UI",
                "UnityEngine.UI",
                "CanvasScaler",
                error) ||
            !resolve_method(
                scaler_mode_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "CanvasScaler",
                    .method_name = "set_uiScaleMode",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.UI.CanvasScaler.ScaleMode"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                scaler_reference_resolution_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "CanvasScaler",
                    .method_name = "set_referenceResolution",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector2"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                scaler_match_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "CanvasScaler",
                    .method_name = "set_matchWidthOrHeight",
                    .return_type = "System.Void",
                    .parameter_types = {"System.Single"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        scaler_ready_ = true;
        return true;
    }

    bool ensure_content_size_fitter(std::string &error) {
        if (!ensure_core(error))
            return false;
        if (content_size_fitter_ready_)
            return true;
        if (!resolve_class(
                content_size_fitter_class_,
                "UnityEngine.UI",
                "UnityEngine.UI",
                "ContentSizeFitter",
                error) ||
            !resolve_method(
                content_size_horizontal_fit_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "ContentSizeFitter",
                    .method_name = "set_horizontalFit",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.UI.ContentSizeFitter.FitMode"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                content_size_vertical_fit_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "ContentSizeFitter",
                    .method_name = "set_verticalFit",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.UI.ContentSizeFitter.FitMode"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        content_size_fitter_ready_ = true;
        return true;
    }

    bool ensure_graphic(std::string &error) {
        if (!ensure_core(error))
            return false;
        if (graphic_ready_)
            return true;
        if (!resolve_class(image_class_, "UnityEngine.UI", "UnityEngine.UI", "Image", error) ||
            !resolve_class(graphic_class_, "UnityEngine.UI", "UnityEngine.UI", "Graphic", error) ||
            !resolve_method(
                raycast_target_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "Graphic",
                    .method_name = "set_raycastTarget",
                    .return_type = "System.Void",
                    .parameter_types = {"System.Boolean"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                vertices_dirty_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "Graphic",
                    .method_name = "SetVerticesDirty",
                    .return_type = "System.Void",
                    .parameter_types = {},
                    .is_static = false,
                },
                error) ||
            !resolve_field_offset(graphic_class_, "m_Color", graphic_color_offset_, error)) {
            return false;
        }
        graphic_ready_ = true;
        return true;
    }

    bool ensure_raw_image(std::string &error) {
        if (!ensure_graphic(error))
            return false;
        if (raw_image_ready_)
            return true;
        if (!resolve_class(
                raw_image_class_,
                "UnityEngine.UI",
                "UnityEngine.UI",
                "RawImage",
                error)) {
            return false;
        }
        raw_image_ready_ = true;
        return true;
    }

    bool ensure_text(std::string &error) {
        if (!ensure_graphic(error))
            return false;
        if (text_ready_)
            return true;
        if (!resolve_class(
                text_class_,
                "Unity.TextMeshPro",
                "TMPro",
                "TextMeshProUGUI",
                error) ||
            !resolve_method(
                text_rect_transform_,
                MethodIdentity{
                    .assembly_name = "Unity.TextMeshPro",
                    .namespace_name = "TMPro",
                    .type_name = "TMP_Text",
                    .method_name = "get_rectTransform",
                    .return_type = "UnityEngine.RectTransform",
                    .parameter_types = {},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                text_value_,
                MethodIdentity{
                    .assembly_name = "Unity.TextMeshPro",
                    .namespace_name = "TMPro",
                    .type_name = "TMP_Text",
                    .method_name = "set_text",
                    .return_type = "System.Void",
                    .parameter_types = {"System.String"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                text_font_size_,
                MethodIdentity{
                    .assembly_name = "Unity.TextMeshPro",
                    .namespace_name = "TMPro",
                    .type_name = "TMP_Text",
                    .method_name = "set_fontSize",
                    .return_type = "System.Void",
                    .parameter_types = {"System.Single"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                text_alignment_,
                MethodIdentity{
                    .assembly_name = "Unity.TextMeshPro",
                    .namespace_name = "TMPro",
                    .type_name = "TMP_Text",
                    .method_name = "set_alignment",
                    .return_type = "System.Void",
                    .parameter_types = {"TMPro.TextAlignmentOptions"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                text_rich_text_,
                MethodIdentity{
                    .assembly_name = "Unity.TextMeshPro",
                    .namespace_name = "TMPro",
                    .type_name = "TMP_Text",
                    .method_name = "set_richText",
                    .return_type = "System.Void",
                    .parameter_types = {"System.Boolean"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                text_line_spacing_,
                MethodIdentity{
                    .assembly_name = "Unity.TextMeshPro",
                    .namespace_name = "TMPro",
                    .type_name = "TMP_Text",
                    .method_name = "set_lineSpacing",
                    .return_type = "System.Void",
                    .parameter_types = {"System.Single"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        text_ready_ = true;
        return true;
    }

    bool ensure_canvas_renderer(std::string &error) {
        if (!ensure_core(error))
            return false;
        if (canvas_renderer_ready_)
            return true;
        if (!resolve_class(
                canvas_renderer_class_,
                "UnityEngine.CoreModule",
                "UnityEngine",
                "CanvasRenderer",
                error)) {
            return false;
        }
        canvas_renderer_ready_ = true;
        return true;
    }

    bool create_game_object(const std::string &name, void **object, std::string &error) {
        if (!ensure_core(error))
            return false;
        if (!allocate_object(game_object_class_, object, error))
            return false;
        void *managed_name = nullptr;
        if (!new_managed_string(name, &managed_name, error))
            return false;
        void *rect_type = nullptr;
        if (!get_type_object(rect_transform_class_.value, &rect_type, error))
            return false;
        void *types[] = {rect_type};
        std::vector<void *> type_values(types, types + 1);
        void *type_array = nullptr;
        if (!allocate_reference_array(system_type_class_, type_values, &type_array, error))
            return false;
        void *args[] = {managed_name, type_array};
        return invoke(game_object_ctor_, *object, args, nullptr, error);
    }

    bool add_component(void *game_object,
                       const ClassSlot &component_class,
                       void **component,
                       std::string &error) {
        if (!ensure_core(error))
            return false;
        if (!require_alive(game_object, "GameObject", error))
            return false;
        void *type_object = nullptr;
        if (!get_type_object(component_class.value, &type_object, error))
            return false;
        void *args[] = {type_object};
        return invoke(add_component_, game_object, args, component, error) &&
            component != nullptr && *component != nullptr;
    }

    bool get_transform(void *game_object, void **transform, std::string &error) {
        if (!require_alive(game_object, "GameObject", error))
            return false;
        return invoke(get_transform_, game_object, nullptr, transform, error) &&
            transform != nullptr && *transform != nullptr;
    }

    bool set_parent(void *transform, void *parent, std::string &error) {
        if (!require_alive(transform, "Transform", error) ||
            !require_alive(parent, "parent Transform", error))
            return false;
        bool world_position_stays = false;
        void *args[] = {parent, &world_position_stays};
        return invoke(set_parent_, transform, args, nullptr, error);
    }

    bool dont_destroy_on_load(void *object, std::string &error) {
        if (!require_alive(object, "Object", error))
            return false;
        void *args[] = {object};
        return invoke(dont_destroy_on_load_, nullptr, args, nullptr, error);
    }

    bool destroy(void *object, std::string &error) {
        if (object == nullptr || !is_alive(object))
            return true;
        void *args[] = {object};
        return invoke(destroy_, nullptr, args, nullptr, error);
    }

    bool is_alive(void *object) const {
        if (object == nullptr)
            return false;
        if (cached_ptr_offset_ < 0)
            return false;
        const auto address = reinterpret_cast<uintptr_t>(object) +
            static_cast<uintptr_t>(cached_ptr_offset_);
        return *reinterpret_cast<void *const *>(address) != nullptr;
    }

    bool require_alive(void *object, const char *name, std::string &error) const {
        if (object == nullptr) {
            error = std::string{name} + " is null";
            return false;
        }
        if (cached_ptr_offset_ < 0) {
            error = "Unity object fake-null check is unavailable";
            return false;
        }
        if (!is_alive(object)) {
            error = std::string{name} + " is no longer alive";
            return false;
        }
        return true;
    }

    bool set_rect(void *rect, Vec2 min, Vec2 max, Vec2 pivot, Vec2 position, Vec2 size, std::string &error) {
        if (!ensure_rect_methods(error))
            return false;
        if (!require_alive(rect, "RectTransform", error))
            return false;
        void *args_min[] = {&min};
        void *args_max[] = {&max};
        void *args_pivot[] = {&pivot};
        void *args_position[] = {&position};
        void *args_size[] = {&size};
        return invoke(rect_anchor_min_, rect, args_min, nullptr, error) &&
            invoke(rect_anchor_max_, rect, args_max, nullptr, error) &&
            invoke(rect_pivot_, rect, args_pivot, nullptr, error) &&
            invoke(rect_anchored_position_, rect, args_position, nullptr, error) &&
            invoke(rect_size_delta_, rect, args_size, nullptr, error);
    }

    bool set_anchors(void *rect, Vec2 min, Vec2 max, std::string &error) {
        if (!ensure_rect_methods(error))
            return false;
        if (!require_alive(rect, "RectTransform", error))
            return false;
        void *args_min[] = {&min};
        void *args_max[] = {&max};
        return invoke(rect_anchor_min_, rect, args_min, nullptr, error) &&
            invoke(rect_anchor_max_, rect, args_max, nullptr, error);
    }

    bool set_pivot(void *rect, Vec2 pivot, std::string &error) {
        if (!ensure_rect_methods(error))
            return false;
        if (!require_alive(rect, "RectTransform", error))
            return false;
        void *args[] = {&pivot};
        return invoke(rect_pivot_, rect, args, nullptr, error);
    }

    bool set_local_scale(void *rect, Vec2 scale, float z, std::string &error) {
        if (!ensure_rect_methods(error) || !resolve_local_scale(error))
            return false;
        if (!require_alive(rect, "Transform", error))
            return false;
        struct Vec3 { float x; float y; float z; } value{scale.x, scale.y, z};
        void *args[] = {&value};
        return invoke(rect_local_scale_, rect, args, nullptr, error);
    }

    bool set_canvas_render_mode(void *canvas, int32_t mode, std::string &error) {
        if (!ensure_canvas(error))
            return false;
        if (!require_alive(canvas, "Canvas", error))
            return false;
        int32_t value = mode;
        void *args[] = {&value};
        return invoke(canvas_render_mode_, canvas, args, nullptr, error);
    }

    bool set_canvas_sorting_order(void *canvas, int32_t order, std::string &error) {
        if (!ensure_canvas(error))
            return false;
        if (!require_alive(canvas, "Canvas", error))
            return false;
        int32_t value = order;
        void *args[] = {&value};
        return invoke(canvas_sorting_order_, canvas, args, nullptr, error);
    }

    bool set_canvas_scale_mode(void *scaler, int32_t mode, std::string &error) {
        if (!ensure_scaler(error))
            return false;
        if (!require_alive(scaler, "CanvasScaler", error))
            return false;
        int32_t value = mode;
        void *args[] = {&value};
        return invoke(scaler_mode_, scaler, args, nullptr, error);
    }

    bool set_canvas_reference_resolution(void *scaler, Vec2 value, std::string &error) {
        if (!ensure_scaler(error))
            return false;
        if (!require_alive(scaler, "CanvasScaler", error))
            return false;
        void *args[] = {&value};
        return invoke(scaler_reference_resolution_, scaler, args, nullptr, error);
    }

    bool set_canvas_match(void *scaler, float value, std::string &error) {
        if (!ensure_scaler(error))
            return false;
        if (!require_alive(scaler, "CanvasScaler", error))
            return false;
        void *args[] = {&value};
        return invoke(scaler_match_, scaler, args, nullptr, error);
    }

    bool set_content_size_horizontal_fit(void *fitter, int32_t mode, std::string &error) {
        if (!ensure_content_size_fitter(error))
            return false;
        if (!require_alive(fitter, "ContentSizeFitter", error))
            return false;
        void *args[] = {&mode};
        return invoke(content_size_horizontal_fit_, fitter, args, nullptr, error);
    }

    bool set_content_size_vertical_fit(void *fitter, int32_t mode, std::string &error) {
        if (!ensure_content_size_fitter(error))
            return false;
        if (!require_alive(fitter, "ContentSizeFitter", error))
            return false;
        void *args[] = {&mode};
        return invoke(content_size_vertical_fit_, fitter, args, nullptr, error);
    }

    bool set_graphic_color(void *graphic, Color value, std::string &error) {
        if (!ensure_graphic(error))
            return false;
        if (!require_alive(graphic, "Graphic", error))
            return false;
        if (graphic_color_offset_ < 0) {
            error = "Graphic.m_Color is unavailable";
            return false;
        }
        std::memcpy(
            reinterpret_cast<char *>(graphic) + graphic_color_offset_,
            &value,
            sizeof(value));
        return invoke(vertices_dirty_, graphic, nullptr, nullptr, error);
    }

    bool set_graphic_raycast_target(void *graphic, bool enabled, std::string &error) {
        if (!ensure_graphic(error))
            return false;
        if (!require_alive(graphic, "Graphic", error))
            return false;
        bool value = enabled;
        void *args[] = {&value};
        return invoke(raycast_target_, graphic, args, nullptr, error);
    }

    bool set_image_sprite(void *image, void *sprite, std::string &error) {
        if (!ensure_graphic(error) ||
            !resolve_method(
                image_sprite_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "Image",
                    .method_name = "set_sprite",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Sprite"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        if (!require_alive(image, "Image", error))
            return false;
        void *args[] = {sprite};
        return invoke(image_sprite_, image, args, nullptr, error);
    }

    bool set_raw_image_texture(void *raw_image, void *texture, std::string &error) {
        if (!ensure_raw_image(error) ||
            !resolve_method(
                raw_image_texture_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "RawImage",
                    .method_name = "set_texture",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Texture"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        if (!require_alive(raw_image, "RawImage", error))
            return false;
        void *args[] = {texture};
        return invoke(raw_image_texture_, raw_image, args, nullptr, error);
    }

    bool set_graphic_material(void *graphic, void *material, std::string &error) {
        if (!ensure_graphic(error) ||
            !resolve_method(
                graphic_material_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.UI",
                    .namespace_name = "UnityEngine.UI",
                    .type_name = "Graphic",
                    .method_name = "set_material",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Material"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        if (!require_alive(graphic, "Graphic", error))
            return false;
        void *args[] = {material};
        return invoke(graphic_material_, graphic, args, nullptr, error);
    }

    bool set_text_resource(
        void *text,
        pccompat_recipe::UiResourceTarget target,
        void *resource,
        std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        MethodSlot *slot = nullptr;
        MethodIdentity identity;
        switch (target) {
        case pccompat_recipe::UiResourceTarget::TextFont:
            slot = &text_font_;
            identity = MethodIdentity{
                .assembly_name = "Unity.TextMeshPro",
                .namespace_name = "TMPro",
                .type_name = "TMP_Text",
                .method_name = "set_font",
                .return_type = "System.Void",
                .parameter_types = {"TMPro.TMP_FontAsset"},
                .is_static = false,
            };
            break;
        case pccompat_recipe::UiResourceTarget::TextFontSharedMaterial:
            slot = &text_font_shared_material_;
            identity = MethodIdentity{
                .assembly_name = "Unity.TextMeshPro",
                .namespace_name = "TMPro",
                .type_name = "TMP_Text",
                .method_name = "set_fontSharedMaterial",
                .return_type = "System.Void",
                .parameter_types = {"UnityEngine.Material"},
                .is_static = false,
            };
            break;
        case pccompat_recipe::UiResourceTarget::TextFontMaterial:
            slot = &text_font_material_;
            identity = MethodIdentity{
                .assembly_name = "Unity.TextMeshPro",
                .namespace_name = "TMPro",
                .type_name = "TMP_Text",
                .method_name = "set_fontMaterial",
                .return_type = "System.Void",
                .parameter_types = {"UnityEngine.Material"},
                .is_static = false,
            };
            break;
        default:
            error = "resource target is not a TMP property";
            return false;
        }
        if (!resolve_method(*slot, identity, error))
            return false;
        void *args[] = {resource};
        return invoke(*slot, text, args, nullptr, error);
    }

    bool set_text(void *text, const std::string &value, std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        if (value.size() > 16384) {
            error = "presentation text exceeds bounded length";
            return false;
        }
        void *managed_string = nullptr;
        if (!new_managed_string(value, &managed_string, error))
            return false;
        void *args[] = {managed_string};
        return invoke(text_value_, text, args, nullptr, error);
    }

    bool set_text_font_size(void *text, float value, std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        void *args[] = {&value};
        return invoke(text_font_size_, text, args, nullptr, error);
    }

    bool set_text_alignment(void *text, int32_t value, std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        void *args[] = {&value};
        return invoke(text_alignment_, text, args, nullptr, error);
    }

    bool set_text_rich_text(void *text, bool enabled, std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        bool value = enabled;
        void *args[] = {&value};
        return invoke(text_rich_text_, text, args, nullptr, error);
    }

    bool set_text_line_spacing(void *text, float value, std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        void *args[] = {&value};
        return invoke(text_line_spacing_, text, args, nullptr, error);
    }

    bool get_text_rect_transform(void *text, void **rect, std::string &error) {
        if (!ensure_text(error))
            return false;
        if (!require_alive(text, "TextMeshProUGUI", error))
            return false;
        return invoke(text_rect_transform_, text, nullptr, rect, error) &&
            rect != nullptr && *rect != nullptr;
    }

    bool ensure_ready_for_component(uint32_t component_mask, std::string &error) {
        if ((component_mask & pccompat_recipe::UiComponentCanvas) != 0 &&
            !ensure_canvas(error))
            return false;
        if ((component_mask & pccompat_recipe::UiComponentCanvasScaler) != 0 &&
            !ensure_scaler(error))
            return false;
        if ((component_mask & pccompat_recipe::UiComponentContentSizeFitter) != 0 &&
            !ensure_content_size_fitter(error))
            return false;
        if ((component_mask & pccompat_recipe::UiComponentImage) != 0 &&
            !ensure_graphic(error))
            return false;
        if ((component_mask & pccompat_recipe::UiComponentRawImage) != 0 &&
            !ensure_raw_image(error))
            return false;
        if ((component_mask & pccompat_recipe::UiComponentTextMeshPro) != 0 &&
            !ensure_text(error))
            return false;
        if ((component_mask & pccompat_recipe::UiComponentCanvasRenderer) != 0 &&
            !ensure_canvas_renderer(error))
            return false;
        return ensure_core(error);
    }

    const ClassSlot &canvas_class() const { return canvas_class_; }
    const ClassSlot &scaler_class() const { return canvas_scaler_class_; }
    const ClassSlot &content_size_fitter_class() const { return content_size_fitter_class_; }
    const ClassSlot &image_class() const { return image_class_; }
    const ClassSlot &raw_image_class() const { return raw_image_class_; }
    const ClassSlot &text_class() const { return text_class_; }
    const ClassSlot &canvas_renderer_class() const { return canvas_renderer_class_; }

private:
    static bool resolve_class(
        ClassSlot &slot,
        const std::string &assembly,
        const std::string &namespaze,
        const std::string &name,
        std::string &error) {
        return slot.value.klass != nullptr ||
            pccompat_metadata::resolve_class(assembly, namespaze, name, slot.value, error);
    }

    static bool resolve_method(
        MethodSlot &slot,
        const MethodIdentity &identity,
        std::string &error) {
        if (slot.value.method_info != nullptr)
            return true;
        return pccompat_metadata::resolve_method(identity, slot.value, error);
    }

    bool ensure_rect_methods(std::string &error) {
        if (rect_ready_)
            return true;
        if (!ensure_core(error) ||
            !resolve_method(
                rect_anchor_min_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "RectTransform",
                    .method_name = "set_anchorMin",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector2"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                rect_anchor_max_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "RectTransform",
                    .method_name = "set_anchorMax",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector2"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                rect_pivot_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "RectTransform",
                    .method_name = "set_pivot",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector2"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                rect_anchored_position_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "RectTransform",
                    .method_name = "set_anchoredPosition",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector2"},
                    .is_static = false,
                },
                error) ||
            !resolve_method(
                rect_size_delta_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "RectTransform",
                    .method_name = "set_sizeDelta",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector2"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        rect_ready_ = true;
        return true;
    }

    bool resolve_local_scale(std::string &error) {
        if (local_scale_ready_)
            return true;
        if (!resolve_method(
                rect_local_scale_,
                MethodIdentity{
                    .assembly_name = "UnityEngine.CoreModule",
                    .namespace_name = "UnityEngine",
                    .type_name = "Transform",
                    .method_name = "set_localScale",
                    .return_type = "System.Void",
                    .parameter_types = {"UnityEngine.Vector3"},
                    .is_static = false,
                },
                error)) {
            return false;
        }
        local_scale_ready_ = true;
        return true;
    }

    static bool invoke(
        const MethodSlot &method,
        void *instance,
        void **args,
        void **result,
        std::string &error) {
        return pccompat_metadata::runtime_invoke(method.value, instance, args, result, error);
    }

    static bool allocate_object(
        const ClassSlot &klass,
        void **object,
        std::string &error) {
        return pccompat_metadata::allocate_object(klass.value, object, error);
    }

    static bool allocate_reference_array(
        const ClassSlot &element_class,
        const std::vector<void *> &elements,
        void **array,
        std::string &error) {
        return pccompat_metadata::allocate_reference_array(
            element_class.value,
            elements,
            array,
            error);
    }

    static bool get_type_object(
        const ResolvedClass &klass,
        void **type_object,
        std::string &error) {
        return pccompat_metadata::get_type_object(klass, type_object, error);
    }

    static bool new_managed_string(
        const std::string &value,
        void **managed_string,
        std::string &error) {
        return pccompat_metadata::new_managed_string(value, managed_string, error);
    }

    static bool resolve_field_offset(
        const ClassSlot &klass,
        const std::string &name,
        int32_t &offset,
        std::string &error) {
        return pccompat_metadata::resolve_field_offset(klass.value, name, offset, error);
    }

    bool core_ready_ = false;
    bool canvas_ready_ = false;
    bool scaler_ready_ = false;
    bool content_size_fitter_ready_ = false;
    bool graphic_ready_ = false;
    bool raw_image_ready_ = false;
    bool text_ready_ = false;
    bool canvas_renderer_ready_ = false;
    bool rect_ready_ = false;
    bool local_scale_ready_ = false;

    ClassSlot core_module_;
    ClassSlot game_object_class_;
    ClassSlot transform_class_;
    ClassSlot rect_transform_class_;
    ClassSlot system_type_class_;
    ClassSlot canvas_class_;
    ClassSlot canvas_scaler_class_;
    ClassSlot content_size_fitter_class_;
    ClassSlot image_class_;
    ClassSlot raw_image_class_;
    ClassSlot graphic_class_;
    ClassSlot text_class_;
    ClassSlot canvas_renderer_class_;

    MethodSlot game_object_ctor_;
    MethodSlot add_component_;
    MethodSlot get_transform_;
    MethodSlot set_parent_;
    MethodSlot dont_destroy_on_load_;
    MethodSlot destroy_;
    MethodSlot rect_anchor_min_;
    MethodSlot rect_anchor_max_;
    MethodSlot rect_pivot_;
    MethodSlot rect_anchored_position_;
    MethodSlot rect_size_delta_;
    MethodSlot rect_local_scale_;
    MethodSlot canvas_render_mode_;
    MethodSlot canvas_sorting_order_;
    MethodSlot scaler_mode_;
    MethodSlot scaler_reference_resolution_;
    MethodSlot scaler_match_;
    MethodSlot content_size_horizontal_fit_;
    MethodSlot content_size_vertical_fit_;
    MethodSlot raycast_target_;
    MethodSlot vertices_dirty_;
    MethodSlot image_sprite_;
    MethodSlot raw_image_texture_;
    MethodSlot graphic_material_;
    MethodSlot text_rect_transform_;
    MethodSlot text_value_;
    MethodSlot text_font_size_;
    MethodSlot text_alignment_;
    MethodSlot text_rich_text_;
    MethodSlot text_line_spacing_;
    MethodSlot text_font_;
    MethodSlot text_font_shared_material_;
    MethodSlot text_font_material_;

    int32_t cached_ptr_offset_ = -1;
    int32_t graphic_color_offset_ = -1;
};

struct NodeRuntime {
    uint32_t id = 0;
    uint32_t parent_id = 0;
    uint32_t components = 0;
    uint32_t flags = 0;
    void *game_object = nullptr;
    void *rect_transform = nullptr;
    void *canvas = nullptr;
    void *canvas_scaler = nullptr;
    void *content_size_fitter = nullptr;
    void *image = nullptr;
    void *raw_image = nullptr;
    void *text = nullptr;
    void *canvas_renderer = nullptr;
    bool parented = false;
    bool active_set = false;
    bool invalid = false;
};

struct RegisteredGraph {
    uint32_t bundle_id = 0;
    std::string mod_id;
    std::vector<pccompat_recipe::UiObjectNode> definitions;
    std::vector<pccompat_recipe::UiResourceBinding> resources;
    bool presentation_enabled = true;
};

enum class MaterializationPhase : uint8_t {
    Idle,
    CreateNodes,
    InitializeNodes,
    ActivateCanvases,
    Complete,
};

enum class NodeBuildStep : uint8_t {
    EnsureTypes,
    CreateObject,
    GetTransform,
    AddContentSizeFitter,
    AddImage,
    AddRawImage,
    AddText,
    AddCanvasRenderer,
    SetParent,
    Complete,
};

enum class MaterializationResult : uint8_t {
    Ready,
    Deferred,
    Failed,
};

struct GraphRuntime {
    uint32_t bundle_id = 0;
    std::vector<pccompat_recipe::UiObjectNode> definitions;
    std::vector<pccompat_recipe::UiResourceBinding> resources;
    std::vector<NodeRuntime> nodes;
    std::unordered_map<uint32_t, size_t> node_index;
    std::vector<void *> gc_handles;
    std::vector<uint8_t> resource_applied;
    std::vector<uint8_t> resource_failed;
    std::vector<uint8_t> resource_waiting;
    std::vector<size_t> materialization_order;
    MaterializationPhase materialization_phase = MaterializationPhase::Idle;
    NodeBuildStep materialization_node_step = NodeBuildStep::EnsureTypes;
    size_t materialization_node_cursor = 0;
    size_t materialization_operation_cursor = 0;
    bool materialized = false;
    bool attempted = false;
    bool destroyed = false;
    bool retirement_pending = false;
    std::string last_error;
    uint32_t materialization_failure_count = 0;
    std::chrono::steady_clock::time_point materialization_next_retry{};
};

struct ResourceResolveRequest {
    uint32_t bundle_id = 0;
    size_t binding_index = 0;
    std::string mod_id;
    pccompat_recipe::UiResourceBinding binding;
};

template <typename T, size_t Capacity>
struct FixedBatch {
    std::array<T, Capacity> values{};
    size_t count = 0;

    bool full() const {
        return count == Capacity;
    }

    template <typename U>
    void push(U &&value) {
        values[count++] = std::forward<U>(value);
    }
};

std::mutex g_graph_lock;
std::vector<RegisteredGraph> g_graphs;
std::vector<GraphRuntime> g_runtime_graphs;
std::vector<GraphRuntime> g_retired_graphs;
constexpr size_t kMaxGraphBundles = 64;
constexpr size_t kMaxRegisteredNodes = 4096;
constexpr size_t kMaxRetiredGraphs = 64;
constexpr size_t kMaxRuntimeGraphs = 128;
constexpr size_t kMaterializationUnityOperationsPerOpportunity = 12;
constexpr size_t kMaxResourceBindingsPerOpportunity = 4;
constexpr size_t kMaxRetiredGraphsPerOpportunity = 4;
UnityApi g_unity_api;
std::atomic<uint32_t> g_materialization_failures{0};
std::atomic<uint32_t> g_invalid_target_count{0};
std::atomic<uint32_t> g_retired_graph_count{0};
std::atomic<uint32_t> g_retirement_pending_hint{0};
std::atomic<uint32_t> g_unsupported_command_count{0};
std::atomic_flag g_resource_resolution_active = ATOMIC_FLAG_INIT;
std::atomic<uint32_t> g_resource_resolution_pending{0};

size_t registered_node_count_locked() {
    size_t count = 0;
    for (const auto &graph : g_graphs)
        count += graph.definitions.size();
    return count;
}

RegisteredGraph *find_definition_locked(uint32_t bundle_id) {
    for (auto &graph : g_graphs) {
        if (graph.bundle_id == bundle_id)
            return &graph;
    }
    return nullptr;
}

GraphRuntime *find_runtime_locked(uint32_t bundle_id) {
    for (auto &graph : g_runtime_graphs) {
        if (graph.bundle_id == bundle_id)
            return &graph;
    }
    return nullptr;
}

NodeRuntime *find_node(GraphRuntime &graph, uint32_t target_id) {
    const auto found = graph.node_index.find(target_id);
    if (found == graph.node_index.end() || found->second >= graph.nodes.size())
        return nullptr;
    return &graph.nodes[found->second];
}

void release_graph_handles(GraphRuntime &graph) {
    for (const auto handle : graph.gc_handles)
        pccompat_metadata::free_gc_handle(handle);
    graph.gc_handles.clear();
}

bool root_graph_object(GraphRuntime &graph, void *object, std::string &error) {
    void *handle = nullptr;
    if (!pccompat_metadata::create_gc_handle(object, handle, error))
        return false;
    graph.gc_handles.push_back(handle);
    return true;
}

void destroy_graph_objects(GraphRuntime &graph) {
    std::string error;
    for (const auto &node : graph.nodes) {
        // Parent destruction cascades through a completed graph. Only roots and
        // nodes whose construction failed before SetParent need direct Destroy.
        if (node.parent_id != 0 && node.parented)
            continue;
        if (node.game_object == nullptr)
            continue;
        if (!g_unity_api.destroy(node.game_object, error) && !error.empty())
            LOGI("destroy presentation graph=%u failed: %s", graph.bundle_id, error.c_str());
        error.clear();
    }
    release_graph_handles(graph);
    graph.nodes.clear();
    graph.node_index.clear();
    graph.definitions.clear();
    graph.resources.clear();
    graph.resource_applied.clear();
    graph.resource_failed.clear();
    graph.resource_waiting.clear();
    graph.materialization_order.clear();
    graph.materialization_phase = MaterializationPhase::Idle;
    graph.materialization_node_step = NodeBuildStep::EnsureTypes;
    graph.materialization_node_cursor = 0;
    graph.materialization_operation_cursor = 0;
    graph.materialized = false;
    graph.destroyed = true;
}

void *node_graphic(NodeRuntime &node) {
    if (node.image != nullptr)
        return node.image;
    if (node.raw_image != nullptr)
        return node.raw_image;
    return node.text;
}

bool apply_component_operation(
    GraphRuntime &graph,
    NodeRuntime &node,
    const pccompat_recipe::UiComponentOperation &operation,
    std::string &error) {
    using Op = pccompat_recipe::UiComponentOpCode;
    switch (operation.op_code) {
    case Op::SetActive:
        node.active_set = true;
        // Runtime recipe presentation is consumed from Canvas callbacks. Calling
        // GameObject.SetActive from that phase can re-enter Unity's canvas
        // lifecycle and abort inside IL2CPP on Android. Graph-level visibility is
        // handled by materialize/destroy instead; per-node active operations are
        // accepted as recipe intent but do not call Unity here.
        return true;
    case Op::SetRect:
        return g_unity_api.set_rect(
            node.rect_transform,
            Vec2{0.0f, 1.0f},
            Vec2{0.0f, 1.0f},
            Vec2{0.0f, 1.0f},
            Vec2{payload_float(operation.payload0), -payload_float(operation.payload1)},
            Vec2{payload_float(operation.payload2), payload_float(operation.payload3)},
            error);
    case Op::SetAnchors:
        return g_unity_api.set_anchors(
            node.rect_transform,
            Vec2{payload_float(operation.payload0), payload_float(operation.payload1)},
            Vec2{payload_float(operation.payload2), payload_float(operation.payload3)},
            error);
    case Op::SetPivot:
        return g_unity_api.set_pivot(
            node.rect_transform,
            Vec2{payload_float(operation.payload0), payload_float(operation.payload1)},
            error);
    case Op::SetLocalScale:
        return g_unity_api.set_local_scale(
            node.rect_transform,
            Vec2{payload_float(operation.payload0), payload_float(operation.payload1)},
            payload_float(operation.payload2),
            error);
    case Op::SetCanvasRenderMode:
        return g_unity_api.set_canvas_render_mode(node.canvas, payload_int(operation.payload0), error);
    case Op::SetCanvasSortingOrder:
        return g_unity_api.set_canvas_sorting_order(node.canvas, payload_int(operation.payload0), error);
    case Op::SetCanvasScaleMode:
        return g_unity_api.set_canvas_scale_mode(node.canvas, payload_int(operation.payload0), error);
    case Op::SetCanvasReferenceResolution:
        return g_unity_api.set_canvas_reference_resolution(
            node.canvas_scaler,
            Vec2{payload_float(operation.payload0), payload_float(operation.payload1)},
            error);
    case Op::SetCanvasMatch:
        return g_unity_api.set_canvas_match(node.canvas_scaler, payload_float(operation.payload0), error);
    case Op::SetGraphicColor:
        return g_unity_api.set_graphic_color(
            node_graphic(node),
            Color{
                payload_float(operation.payload0),
                payload_float(operation.payload1),
                payload_float(operation.payload2),
                payload_float(operation.payload3)},
            error);
    case Op::SetGraphicRaycastTarget:
        return g_unity_api.set_graphic_raycast_target(
            node_graphic(node),
            payload_bool(operation.payload0),
            error);
    case Op::SetText:
        return g_unity_api.set_text(node.text, operation.string_value, error);
    case Op::SetTextFontSize:
        return g_unity_api.set_text_font_size(node.text, payload_float(operation.payload0), error);
    case Op::SetTextAlignment:
        return g_unity_api.set_text_alignment(node.text, payload_int(operation.payload0), error);
    case Op::SetTextRichText:
        return g_unity_api.set_text_rich_text(node.text, payload_bool(operation.payload0), error);
    case Op::SetTextLineSpacing:
        return g_unity_api.set_text_line_spacing(
            node.text,
            payload_float(operation.payload0),
            error);
    case Op::SetContentSizeHorizontalFit:
        return g_unity_api.set_content_size_horizontal_fit(
            node.content_size_fitter,
            payload_int(operation.payload0),
            error);
    case Op::SetContentSizeVerticalFit:
        return g_unity_api.set_content_size_vertical_fit(
            node.content_size_fitter,
            payload_int(operation.payload0),
            error);
    }
    error = "unsupported component operation";
    return false;
}

bool apply_resource_to_node(
    NodeRuntime &node,
    pccompat_recipe::UiResourceTarget target,
    void *resource,
    std::string &error) {
    using Target = pccompat_recipe::UiResourceTarget;
    switch (target) {
    case Target::ImageSprite:
        if (node.image == nullptr) {
            error = "resource target has no Image component";
            return false;
        }
        return g_unity_api.set_image_sprite(node.image, resource, error);
    case Target::RawImageTexture:
        if (node.raw_image == nullptr) {
            error = "resource target has no RawImage component";
            return false;
        }
        return g_unity_api.set_raw_image_texture(node.raw_image, resource, error);
    case Target::GraphicMaterial: {
        auto *graphic = node_graphic(node);
        if (graphic == nullptr) {
            error = "resource target has no Graphic component";
            return false;
        }
        return g_unity_api.set_graphic_material(graphic, resource, error);
    }
    case Target::TextFont:
    case Target::TextFontSharedMaterial:
    case Target::TextFontMaterial:
        if (node.text == nullptr) {
            error = "resource target has no TMP_Text component";
            return false;
        }
        return g_unity_api.set_text_resource(node.text, target, resource, error);
    }
    error = "unsupported UI resource target";
    return false;
}

FixedBatch<ResourceResolveRequest, kMaxResourceBindingsPerOpportunity>
collect_pending_resource_requests_locked(
    bool &has_more) {
    FixedBatch<ResourceResolveRequest, kMaxResourceBindingsPerOpportunity> requests;
    has_more = false;
    for (auto &runtime : g_runtime_graphs) {
        if (!runtime.materialized || runtime.retirement_pending || runtime.resources.empty())
            continue;
        RegisteredGraph *definition = find_definition_locked(runtime.bundle_id);
        if (definition == nullptr || !definition->presentation_enabled)
            continue;
        if (runtime.resource_applied.size() != runtime.resources.size())
            runtime.resource_applied.assign(runtime.resources.size(), 0);
        if (runtime.resource_failed.size() != runtime.resources.size())
            runtime.resource_failed.assign(runtime.resources.size(), 0);
        if (runtime.resource_waiting.size() != runtime.resources.size())
            runtime.resource_waiting.assign(runtime.resources.size(), 0);

        for (size_t index = 0; index < runtime.resources.size(); ++index) {
            if (runtime.resource_applied[index] != 0 ||
                runtime.resource_failed[index] != 0 ||
                runtime.resource_waiting[index] != 0)
                continue;
            const auto &binding = runtime.resources[index];
            NodeRuntime *node = find_node(runtime, binding.node_id);
            if (node == nullptr || node->invalid) {
                runtime.resource_failed[index] = 1;
                LOGE("resource binding target missing bundle=%u node=%u asset=%s",
                     runtime.bundle_id,
                     binding.node_id,
                     binding.asset_name.c_str());
                continue;
            }
            if (requests.full()) {
                has_more = true;
                return requests;
            }
            requests.push(ResourceResolveRequest{
                .bundle_id = runtime.bundle_id,
                .binding_index = index,
                .mod_id = definition->mod_id,
                .binding = binding,
            });
        }
    }
    return requests;
}

bool resource_binding_matches(
    const pccompat_recipe::UiResourceBinding &left,
    const pccompat_recipe::UiResourceBinding &right) {
    return left.node_id == right.node_id &&
        left.target == right.target &&
        left.feature_group_id == right.feature_group_id &&
        left.asset_name == right.asset_name &&
        left.expected_type == right.expected_type;
}

void resolve_pending_resources_on_unity_main() {
    const auto resolver = g_resource_resolver.load(std::memory_order_acquire);
    if (resolver == nullptr)
        return;
    if (g_resource_resolution_active.test_and_set(std::memory_order_acquire)) {
        g_resource_resolution_pending.store(1, std::memory_order_release);
        return;
    }
    struct ResolutionReset final {
        ~ResolutionReset() {
            g_resource_resolution_active.clear(std::memory_order_release);
        }
    } reset;

    FixedBatch<ResourceResolveRequest, kMaxResourceBindingsPerOpportunity> requests;
    bool has_more = false;
    {
        std::lock_guard<std::mutex> guard(g_graph_lock);
        requests = collect_pending_resource_requests_locked(has_more);
    }
    if (has_more)
        g_resource_resolution_pending.store(1, std::memory_order_release);

    for (size_t request_index = 0; request_index < requests.count; ++request_index) {
        const auto &request = requests.values[request_index];
        void *asset = nullptr;
        // Never invoke managed code while g_graph_lock is held. The callback may
        // enqueue UnityMain work and can synchronously cross back into native code.
        const int status = resolver(
            request.mod_id.c_str(),
            request.binding.feature_group_id.c_str(),
            request.binding.asset_name.c_str(),
            request.binding.expected_type.c_str(),
            &asset);
        std::lock_guard<std::mutex> guard(g_graph_lock);
        RegisteredGraph *definition = find_definition_locked(request.bundle_id);
        GraphRuntime *runtime = find_runtime_locked(request.bundle_id);
        if (definition == nullptr || !definition->presentation_enabled || runtime == nullptr ||
            definition->mod_id != request.mod_id ||
            !runtime->materialized || runtime->retirement_pending ||
            request.binding_index >= runtime->resources.size() ||
            request.binding_index >= runtime->resource_applied.size() ||
            request.binding_index >= runtime->resource_failed.size() ||
            request.binding_index >= runtime->resource_waiting.size() ||
            !resource_binding_matches(
                runtime->resources[request.binding_index], request.binding) ||
            runtime->resource_applied[request.binding_index] != 0 ||
            runtime->resource_failed[request.binding_index] != 0)
            continue;

        if (status == 1) {
            // Pending: the managed side queued or is loading the asset; retry
            // after the next merged UnityMain refresh clears the waiting bit.
            runtime->resource_waiting[request.binding_index] = 1;
            continue;
        }
        if (status != 2) {
            // Hard failure (status 0): the managed resolver proved this asset
            // unavailable. Mark it failed instead of re-entering the managed
            // bridge on every refresh forever.
            runtime->resource_failed[request.binding_index] = 1;
            LOGE("resource resolver failed closed mod=%s asset=%s type=%s",
                 request.mod_id.c_str(),
                 request.binding.asset_name.c_str(),
                 request.binding.expected_type.c_str());
            continue;
        }

        const auto &binding = runtime->resources[request.binding_index];
        NodeRuntime *node = find_node(*runtime, binding.node_id);
        if (asset == nullptr) {
            runtime->resource_failed[request.binding_index] = 1;
            LOGE("resource resolver returned ready/null mod=%s asset=%s",
                 definition->mod_id.c_str(),
                 binding.asset_name.c_str());
            continue;
        }
        if (node == nullptr || node->invalid) {
            runtime->resource_failed[request.binding_index] = 1;
            LOGE("resource binding target disappeared bundle=%u node=%u asset=%s",
                 runtime->bundle_id,
                 binding.node_id,
                 binding.asset_name.c_str());
            continue;
        }

        std::string error;
        if (!apply_resource_to_node(*node, binding.target, asset, error)) {
            runtime->resource_failed[request.binding_index] = 1;
            LOGE("resource apply failed mod=%s node=%u asset=%s error=%s",
                 definition->mod_id.c_str(),
                 binding.node_id,
                 binding.asset_name.c_str(),
                 error.empty() ? "<unknown>" : error.c_str());
            continue;
        }
        runtime->resource_applied[request.binding_index] = 1;
        LOGI("resource applied mod=%s node=%u asset=%s target=%u",
             definition->mod_id.c_str(),
             binding.node_id,
             binding.asset_name.c_str(),
             static_cast<uint32_t>(binding.target));
    }
}

void clear_applied_resources(GraphRuntime &runtime, const RegisteredGraph &definition) {
    if (!runtime.materialized || runtime.resources.empty())
        return;
    if (runtime.resource_applied.size() != runtime.resources.size())
        runtime.resource_applied.assign(runtime.resources.size(), 0);
    for (size_t index = 0; index < runtime.resources.size(); ++index) {
        if (runtime.resource_applied[index] == 0)
            continue;
        NodeRuntime *node = find_node(runtime, runtime.resources[index].node_id);
        std::string error;
        if (node != nullptr &&
            !apply_resource_to_node(*node, runtime.resources[index].target, nullptr, error)) {
            LOGI("resource clear failed mod=%s node=%u error=%s",
                 definition.mod_id.c_str(),
                 runtime.resources[index].node_id,
                 error.empty() ? "<unknown>" : error.c_str());
        }
        runtime.resource_applied[index] = 0;
    }
    std::fill(runtime.resource_failed.begin(), runtime.resource_failed.end(), 0);
    std::fill(runtime.resource_waiting.begin(), runtime.resource_waiting.end(), 0);
}

bool is_canvas_operation(pccompat_recipe::UiComponentOpCode op_code) {
    using Op = pccompat_recipe::UiComponentOpCode;
    return op_code == Op::SetCanvasRenderMode ||
        op_code == Op::SetCanvasSortingOrder ||
        op_code == Op::SetCanvasScaleMode ||
        op_code == Op::SetCanvasReferenceResolution ||
        op_code == Op::SetCanvasMatch;
}

bool prepare_materialization(GraphRuntime &runtime,
                             const RegisteredGraph &definition,
                             std::string &error) {
    runtime.definitions = definition.definitions;
    runtime.resources = definition.resources;
    runtime.nodes.clear();
    runtime.node_index.clear();
    runtime.gc_handles.clear();
    runtime.resource_applied.assign(runtime.resources.size(), 0);
    runtime.resource_failed.assign(runtime.resources.size(), 0);
    runtime.resource_waiting.assign(runtime.resources.size(), 0);
    runtime.attempted = true;
    runtime.destroyed = false;
    runtime.materialization_phase = MaterializationPhase::CreateNodes;
    runtime.materialization_node_step = NodeBuildStep::EnsureTypes;
    runtime.materialization_node_cursor = 0;
    runtime.materialization_operation_cursor = 0;

    if (runtime.definitions.empty()) {
        runtime.materialized = true;
        runtime.materialization_phase = MaterializationPhase::Complete;
        return true;
    }
    if (!g_unity_api.ensure_core(error))
        return false;

    runtime.nodes.resize(runtime.definitions.size());
    for (size_t index = 0; index < runtime.definitions.size(); ++index) {
        const auto &definition_node = runtime.definitions[index];
        runtime.nodes[index].id = definition_node.id;
        runtime.nodes[index].parent_id = definition_node.parent_id;
        runtime.nodes[index].components = definition_node.components;
        runtime.nodes[index].flags = definition_node.flags;
        if (!runtime.node_index.emplace(definition_node.id, index).second) {
            error = "duplicate presentation node id";
            destroy_graph_objects(runtime);
            return false;
        }
    }

    std::vector<uint32_t> child_counts(runtime.definitions.size(), 0);
    for (size_t index = 0; index < runtime.definitions.size(); ++index) {
        const auto parent_id = runtime.definitions[index].parent_id;
        if (parent_id == 0)
            continue;
        const auto parent = runtime.node_index.find(parent_id);
        if (parent == runtime.node_index.end()) {
            error = "presentation node parent is missing";
            destroy_graph_objects(runtime);
            return false;
        }
        ++child_counts[parent->second];
    }
    std::vector<std::vector<size_t>> children(runtime.definitions.size());
    for (size_t index = 0; index < children.size(); ++index)
        children[index].reserve(child_counts[index]);
    std::vector<size_t> ready;
    ready.reserve(runtime.definitions.size());
    for (size_t index = 0; index < runtime.definitions.size(); ++index) {
        const auto parent_id = runtime.definitions[index].parent_id;
        if (parent_id == 0)
            ready.push_back(index);
        else
            children[runtime.node_index.at(parent_id)].push_back(index);
    }
    runtime.materialization_order.reserve(runtime.definitions.size());
    for (size_t cursor = 0; cursor < ready.size(); ++cursor) {
        const size_t index = ready[cursor];
        runtime.materialization_order.push_back(index);
        ready.insert(ready.end(), children[index].begin(), children[index].end());
    }
    if (runtime.materialization_order.size() != runtime.definitions.size()) {
        error = "presentation object graph contains an unresolved parent cycle";
        destroy_graph_objects(runtime);
        return false;
    }
    return true;
}

bool build_node_step(GraphRuntime &runtime,
                     size_t index,
                     bool &did_unity_work,
                     std::string &error) {
    const auto &definition_node = runtime.definitions[index];
    auto &node = runtime.nodes[index];
    did_unity_work = false;
    for (;;) {
        switch (runtime.materialization_node_step) {
    case NodeBuildStep::EnsureTypes:
        if (!g_unity_api.ensure_ready_for_component(definition_node.components, error))
            return false;
        runtime.materialization_node_step = NodeBuildStep::CreateObject;
        did_unity_work = true;
        return true;
    case NodeBuildStep::CreateObject: {
        if (!g_unity_api.create_game_object(definition_node.name, &node.game_object, error))
            return false;
        if (!root_graph_object(runtime, node.game_object, error))
            return false;
        runtime.materialization_node_step = NodeBuildStep::GetTransform;
        did_unity_work = true;
        return true;
    }
    case NodeBuildStep::GetTransform:
        if (!g_unity_api.get_transform(node.game_object, &node.rect_transform, error) ||
            !root_graph_object(runtime, node.rect_transform, error))
            return false;
        runtime.materialization_node_step = NodeBuildStep::AddContentSizeFitter;
        did_unity_work = true;
        return true;
    case NodeBuildStep::AddContentSizeFitter:
        runtime.materialization_node_step = NodeBuildStep::AddImage;
        if ((definition_node.components & pccompat_recipe::UiComponentContentSizeFitter) == 0)
            continue;
        if (!g_unity_api.add_component(
                node.game_object,
                g_unity_api.content_size_fitter_class(),
                &node.content_size_fitter,
                error) ||
            !root_graph_object(runtime, node.content_size_fitter, error))
            return false;
        did_unity_work = true;
        return true;
    case NodeBuildStep::AddImage:
        runtime.materialization_node_step = NodeBuildStep::AddRawImage;
        if ((definition_node.components & pccompat_recipe::UiComponentImage) == 0)
            continue;
        if (!g_unity_api.add_component(
                node.game_object,
                g_unity_api.image_class(),
                &node.image,
                error) ||
            !root_graph_object(runtime, node.image, error))
            return false;
        did_unity_work = true;
        return true;
    case NodeBuildStep::AddRawImage:
        runtime.materialization_node_step = NodeBuildStep::AddText;
        if ((definition_node.components & pccompat_recipe::UiComponentRawImage) == 0)
            continue;
        if (!g_unity_api.add_component(
                node.game_object,
                g_unity_api.raw_image_class(),
                &node.raw_image,
                error) ||
            !root_graph_object(runtime, node.raw_image, error))
            return false;
        did_unity_work = true;
        return true;
    case NodeBuildStep::AddText:
        runtime.materialization_node_step = NodeBuildStep::AddCanvasRenderer;
        if ((definition_node.components & pccompat_recipe::UiComponentTextMeshPro) == 0)
            continue;
        if (!g_unity_api.add_component(
                 node.game_object,
                 g_unity_api.text_class(),
                 &node.text,
                 error) ||
            !root_graph_object(runtime, node.text, error) ||
            !g_unity_api.get_text_rect_transform(node.text, &node.rect_transform, error) ||
            !root_graph_object(runtime, node.rect_transform, error)) {
            return false;
        }
        did_unity_work = true;
        return true;
    case NodeBuildStep::AddCanvasRenderer:
        runtime.materialization_node_step = NodeBuildStep::SetParent;
        if ((definition_node.components & pccompat_recipe::UiComponentCanvasRenderer) == 0 ||
            node.image != nullptr || node.raw_image != nullptr || node.text != nullptr) {
            continue;
        }
        if (!g_unity_api.add_component(
                node.game_object,
                g_unity_api.canvas_renderer_class(),
                &node.canvas_renderer,
                error) ||
            !root_graph_object(runtime, node.canvas_renderer, error))
            return false;
        did_unity_work = true;
        return true;
    case NodeBuildStep::SetParent:
        runtime.materialization_node_step = NodeBuildStep::Complete;
        if (definition_node.parent_id != 0) {
            NodeRuntime *parent = find_node(runtime, definition_node.parent_id);
            if (parent == nullptr || parent->rect_transform == nullptr ||
                !g_unity_api.set_parent(node.rect_transform, parent->rect_transform, error)) {
                return false;
            }
            node.parented = true;
            did_unity_work = true;
        }
        return true;
    case NodeBuildStep::Complete:
        return true;
    }
    error = "presentation node build step is invalid";
    return false;
    }
}

bool initialize_node_step(GraphRuntime &runtime,
                          size_t index,
                          bool &did_unity_work,
                          std::string &error) {
    const auto &definition_node = runtime.definitions[index];
    auto &node = runtime.nodes[index];
    while (runtime.materialization_operation_cursor < definition_node.initialization.size()) {
        const auto &operation =
            definition_node.initialization[runtime.materialization_operation_cursor++];
        if (is_canvas_operation(operation.op_code))
            continue;
        did_unity_work = true;
        return apply_component_operation(runtime, node, operation, error);
    }
    if (runtime.materialization_operation_cursor == definition_node.initialization.size()) {
        ++runtime.materialization_operation_cursor;
        if ((definition_node.flags & pccompat_recipe::UiObjectDontDestroyOnLoad) != 0) {
            did_unity_work = true;
            return g_unity_api.dont_destroy_on_load(node.game_object, error);
        }
    }
    return true;
}

size_t canvas_node_work_cost(const pccompat_recipe::UiObjectNode &definition_node) {
    size_t cost = 1;
    if ((definition_node.components & pccompat_recipe::UiComponentCanvas) != 0)
        ++cost;
    if ((definition_node.components & pccompat_recipe::UiComponentCanvasScaler) != 0)
        ++cost;
    for (const auto &operation : definition_node.initialization) {
        if (is_canvas_operation(operation.op_code))
            ++cost;
    }
    return cost;
}

bool activate_canvas_node(GraphRuntime &runtime,
                          size_t index,
                          std::string &error) {
    const auto &definition_node = runtime.definitions[index];
    auto &node = runtime.nodes[index];
    const uint32_t canvas_components = definition_node.components &
        (pccompat_recipe::UiComponentCanvas | pccompat_recipe::UiComponentCanvasScaler);
    if (canvas_components == 0)
        return true;
    if (!g_unity_api.ensure_ready_for_component(canvas_components, error))
        return false;
    if ((canvas_components & pccompat_recipe::UiComponentCanvas) != 0 &&
        (!g_unity_api.add_component(
             node.game_object,
             g_unity_api.canvas_class(),
             &node.canvas,
             error) ||
         !root_graph_object(runtime, node.canvas, error))) {
        return false;
    }
    if ((canvas_components & pccompat_recipe::UiComponentCanvasScaler) != 0 &&
        (!g_unity_api.add_component(
             node.game_object,
             g_unity_api.scaler_class(),
             &node.canvas_scaler,
             error) ||
         !root_graph_object(runtime, node.canvas_scaler, error))) {
        return false;
    }
    for (const auto &operation : definition_node.initialization) {
        if (is_canvas_operation(operation.op_code) &&
            !apply_component_operation(runtime, node, operation, error)) {
            return false;
        }
    }
    return true;
}

MaterializationResult materialize_graph_step(GraphRuntime &runtime,
                                             const RegisteredGraph &definition,
                                             size_t &budget,
                                             std::string &error) {
    if (runtime.materialization_phase == MaterializationPhase::Idle &&
        !prepare_materialization(runtime, definition, error)) {
        return MaterializationResult::Failed;
    }
    if (runtime.materialization_phase == MaterializationPhase::Complete)
        return MaterializationResult::Ready;

    while (budget != 0) {
        if (runtime.materialization_phase == MaterializationPhase::CreateNodes) {
            if (runtime.materialization_node_cursor >= runtime.materialization_order.size()) {
                runtime.materialization_phase = MaterializationPhase::InitializeNodes;
                runtime.materialization_node_cursor = 0;
                runtime.materialization_operation_cursor = 0;
                continue;
            }
            const size_t index =
                runtime.materialization_order[runtime.materialization_node_cursor];
            bool did_unity_work = false;
            if (!build_node_step(runtime, index, did_unity_work, error))
                return MaterializationResult::Failed;
            if (did_unity_work)
                --budget;
            if (runtime.materialization_node_step == NodeBuildStep::Complete) {
                runtime.materialization_node_step = NodeBuildStep::EnsureTypes;
                ++runtime.materialization_node_cursor;
            }
            continue;
        }
        if (runtime.materialization_phase == MaterializationPhase::InitializeNodes) {
            if (runtime.materialization_node_cursor >= runtime.materialization_order.size()) {
                runtime.materialization_phase = MaterializationPhase::ActivateCanvases;
                runtime.materialization_node_cursor = 0;
                runtime.materialization_operation_cursor = 0;
                continue;
            }
            const size_t index =
                runtime.materialization_order[runtime.materialization_node_cursor];
            bool did_unity_work = false;
            if (!initialize_node_step(runtime, index, did_unity_work, error))
                return MaterializationResult::Failed;
            if (did_unity_work)
                --budget;
            const auto operation_count = runtime.definitions[index].initialization.size();
            if (runtime.materialization_operation_cursor > operation_count) {
                runtime.materialization_operation_cursor = 0;
                ++runtime.materialization_node_cursor;
            }
            continue;
        }
        if (runtime.materialization_phase == MaterializationPhase::ActivateCanvases) {
            if (runtime.materialization_node_cursor >= runtime.materialization_order.size()) {
                runtime.materialization_phase = MaterializationPhase::Complete;
                runtime.materialized = true;
                runtime.destroyed = false;
                if (!runtime.resources.empty())
                    g_resource_resolution_pending.store(1, std::memory_order_release);
                return MaterializationResult::Ready;
            }
            const size_t index =
                runtime.materialization_order[runtime.materialization_node_cursor];
            const auto components = runtime.definitions[index].components;
            if ((components & (pccompat_recipe::UiComponentCanvas |
                               pccompat_recipe::UiComponentCanvasScaler)) == 0) {
                ++runtime.materialization_node_cursor;
                continue;
            }
            const size_t work_cost = canvas_node_work_cost(runtime.definitions[index]);
            if (work_cost > budget &&
                budget != kMaterializationUnityOperationsPerOpportunity) {
                return MaterializationResult::Deferred;
            }
            if (!activate_canvas_node(runtime, index, error))
                return MaterializationResult::Failed;
            budget = work_cost >= budget ? 0 : budget - work_cost;
            ++runtime.materialization_node_cursor;
            continue;
        }
        break;
    }
    return MaterializationResult::Deferred;
}

MaterializationResult ensure_materialized(GraphRuntime &runtime,
                                          const RegisteredGraph &definition,
                                          size_t &budget,
                                          bool validate_existing) {
    if (runtime.materialized) {
        if (!validate_existing)
            return MaterializationResult::Ready;
        for (const auto &node : runtime.nodes) {
            if (!g_unity_api.is_alive(node.game_object)) {
                destroy_graph_objects(runtime);
                break;
            }
        }
        if (runtime.materialized)
            return MaterializationResult::Ready;
    }
    if (runtime.attempted && !runtime.last_error.empty()) {
        // A failed materialization is not permanent: metadata/type availability
        // is startup-transient, so retry with bounded exponential backoff
        // instead of bricking this bundle's UI until DESTROY_GRAPH.
        constexpr uint32_t kMaxMaterializationRetries = 5;
        if (runtime.materialization_failure_count >= kMaxMaterializationRetries ||
            std::chrono::steady_clock::now() < runtime.materialization_next_retry) {
            return MaterializationResult::Failed;
        }
    }
    std::string error;
    const auto result = materialize_graph_step(runtime, definition, budget, error);
    if (result == MaterializationResult::Deferred)
        return result;
    if (result == MaterializationResult::Failed) {
        destroy_graph_objects(runtime);
        runtime.last_error = error;
        const uint32_t failures = ++runtime.materialization_failure_count;
        const auto backoff = std::chrono::milliseconds(500) *
            (1u << std::min(failures - 1, static_cast<uint32_t>(5)));
        runtime.materialization_next_retry = std::chrono::steady_clock::now() + backoff;
        g_materialization_failures.fetch_add(1, std::memory_order_relaxed);
        LOGE("presentation graph materialization failed bundle=%u attempt=%u error=%s",
             runtime.bundle_id,
             failures,
             error.empty() ? "<unknown>" : error.c_str());
        return result;
    }
    runtime.last_error.clear();
    runtime.materialization_failure_count = 0;
    LOGI("presentation graph materialized bundle=%u nodes=%zu",
         runtime.bundle_id,
         runtime.nodes.size());
    return MaterializationResult::Ready;
}

bool set_node_text_from_slot(GraphRuntime &graph,
                             NodeRuntime &node,
                             int64_t slot,
                             std::string &error) {
    if (node.text == nullptr) {
        error = "presentation target has no TextMeshProUGUI component";
        return false;
    }
    const auto found = graph.node_index.find(node.id);
    if (found == graph.node_index.end() || found->second >= graph.definitions.size()) {
        error = "presentation target definition is missing";
        return false;
    }
    const auto &operations = graph.definitions[found->second].initialization;
    if (slot >= 0 && static_cast<size_t>(slot) < operations.size() &&
        operations[static_cast<size_t>(slot)].op_code ==
            pccompat_recipe::UiComponentOpCode::SetText) {
        return g_unity_api.set_text(
            node.text,
            operations[static_cast<size_t>(slot)].string_value,
            error);
    }
    error = "SetText command references an invalid recipe text slot";
    return false;
}

bool apply_presentation_command(const hud_logic::PresentationCommand &command,
                                size_t &materialization_budget) {
    std::lock_guard<std::mutex> guard(g_graph_lock);
    RegisteredGraph *definition = find_definition_locked(command.generation);
    if (definition == nullptr) {
        g_invalid_target_count.fetch_add(1, std::memory_order_relaxed);
        return true;
    }
    if (!definition->presentation_enabled)
        return true;
    GraphRuntime *runtime = find_runtime_locked(command.generation);
    if (runtime == nullptr) {
        if (g_runtime_graphs.size() >= kMaxRuntimeGraphs) {
            g_invalid_target_count.fetch_add(1, std::memory_order_relaxed);
            LOGE("presentation runtime graph capacity exceeded bundle=%u",
                 command.generation);
            return true;
        }
        g_runtime_graphs.push_back(GraphRuntime{.bundle_id = command.generation});
        runtime = &g_runtime_graphs.back();
    }

    if (command.command_type == PC_COMPAT_PRESENTATION_DESTROY_GRAPH) {
        destroy_graph_objects(*runtime);
        runtime->attempted = false;
        runtime->last_error.clear();
        runtime->materialization_failure_count = 0;
        runtime->materialization_next_retry = {};
        return true;
    }
    if (command.command_type == PC_COMPAT_PRESENTATION_SET_ACTIVE &&
        command.payload0 == 0) {
        // Do not call SetActive from Canvas callbacks. Treat hidden recipe HUDs as
        // unloaded graphs; the next visible command will materialize fresh objects.
        if (runtime->materialized || !runtime->nodes.empty())
            destroy_graph_objects(*runtime);
        runtime->attempted = false;
        runtime->last_error.clear();
        runtime->materialization_failure_count = 0;
        runtime->materialization_next_retry = {};
        return true;
    }
    const bool validate_existing =
        command.command_type == PC_COMPAT_PRESENTATION_ENSURE_GRAPH ||
        command.command_type == PC_COMPAT_PRESENTATION_SET_ACTIVE;
    const auto materialization = ensure_materialized(
        *runtime,
        *definition,
        materialization_budget,
        validate_existing);
    if (materialization == MaterializationResult::Deferred)
        return false;
    if (materialization == MaterializationResult::Failed)
        return true;
    if (command.command_type == PC_COMPAT_PRESENTATION_ENSURE_GRAPH)
        return true;
    if (command.command_type == PC_COMPAT_PRESENTATION_SET_ACTIVE)
        return true;

    NodeRuntime *node = find_node(*runtime, command.target_id);
    if (node == nullptr || node->invalid) {
        g_invalid_target_count.fetch_add(1, std::memory_order_relaxed);
        return true;
    }
    if (!g_unity_api.is_alive(node->game_object)) {
        g_invalid_target_count.fetch_add(1, std::memory_order_relaxed);
        destroy_graph_objects(*runtime);
        runtime->attempted = false;
        return true;
    }

    std::string error;
    bool ok = false;
    switch (command.command_type) {
    case PC_COMPAT_PRESENTATION_SET_ACTIVE:
        ok = true;
        break;
    case PC_COMPAT_PRESENTATION_SET_RECT:
        ok = g_unity_api.set_rect(
            node->rect_transform,
            Vec2{0.0f, 1.0f},
            Vec2{0.0f, 1.0f},
            Vec2{0.0f, 1.0f},
            Vec2{command.value0, -command.value1},
            Vec2{payload_float(command.payload0), payload_float(command.payload1)},
            error);
        break;
    case PC_COMPAT_PRESENTATION_SET_TEXT:
        ok = set_node_text_from_slot(*runtime, *node, command.payload0, error);
        break;
    case PC_COMPAT_PRESENTATION_SET_COLOR:
        ok = g_unity_api.set_graphic_color(
            node_graphic(*node),
            Color{
                command.value0,
                command.value1,
                payload_float(command.payload0),
                payload_float(command.payload1)},
            error);
        break;
    case PC_COMPAT_PRESENTATION_SET_FONT_SIZE:
        ok = g_unity_api.set_text_font_size(node->text, command.value0, error);
        break;
    case PC_COMPAT_PRESENTATION_INVALIDATE_TARGET:
        node->invalid = true;
        node->active_set = true;
        ok = true;
        break;
    default:
        g_unsupported_command_count.fetch_add(1, std::memory_order_relaxed);
        return true;
    }
    if (!ok) {
        g_invalid_target_count.fetch_add(1, std::memory_order_relaxed);
        LOGI("presentation command rejected bundle=%u command=%u target=%u error=%s",
             command.generation,
             command.command_type,
             command.target_id,
             error.empty() ? "<unknown>" : error.c_str());
    }
    return true;
}

void drain_retired_graphs() {
    FixedBatch<GraphRuntime, kMaxRetiredGraphsPerOpportunity> retired;
    bool remains = false;
    {
        std::lock_guard<std::mutex> guard(g_graph_lock);
        while (!g_retired_graphs.empty() &&
               !retired.full()) {
            retired.push(std::move(g_retired_graphs.back()));
            g_retired_graphs.pop_back();
        }
        auto runtime = g_runtime_graphs.begin();
        while (runtime != g_runtime_graphs.end()) {
            if (!runtime->retirement_pending) {
                ++runtime;
                continue;
            }
            if (retired.full()) {
                remains = true;
                ++runtime;
                continue;
            }
            retired.push(std::move(*runtime));
            runtime = g_runtime_graphs.erase(runtime);
        }
        remains = remains || !g_retired_graphs.empty();
        g_retirement_pending_hint.store(remains ? 1u : 0u, std::memory_order_release);
    }
    for (size_t index = 0; index < retired.count; ++index)
        destroy_graph_objects(retired.values[index]);
}

}  // namespace

bool register_bundle_graph(
    uint32_t bundle_id,
    const std::string &mod_id,
    const std::vector<pccompat_recipe::UiObjectNode> &nodes,
    const std::vector<pccompat_recipe::UiResourceBinding> &resources,
    std::string &error) {
    if (nodes.empty())
        return true;
    if (bundle_id == 0 || mod_id.empty() || nodes.size() > kMaxRegisteredNodes) {
        error = "presentation object graph identity/capacity is invalid";
        return false;
    }

    std::lock_guard<std::mutex> guard(g_graph_lock);
    for (const auto &graph : g_graphs) {
        if (graph.bundle_id == bundle_id) {
            error = "presentation object graph bundle is already registered";
            return false;
        }
    }
    if (g_graphs.size() >= kMaxGraphBundles ||
        registered_node_count_locked() + nodes.size() > kMaxRegisteredNodes) {
        error = "presentation object graph registry capacity exceeded";
        return false;
    }
    g_graphs.push_back(RegisteredGraph{
        .bundle_id = bundle_id,
        .mod_id = mod_id,
        .definitions = nodes,
        .resources = resources,
    });
    return true;
}

void discard_bundle_graph(uint32_t bundle_id) {
    std::lock_guard<std::mutex> guard(g_graph_lock);
    g_graphs.erase(
        std::remove_if(
            g_graphs.begin(),
            g_graphs.end(),
            [bundle_id](const RegisteredGraph &graph) {
                return graph.bundle_id == bundle_id;
            }),
        g_graphs.end());
    auto runtime = std::find_if(
        g_runtime_graphs.begin(),
        g_runtime_graphs.end(),
        [bundle_id](const GraphRuntime &graph) { return graph.bundle_id == bundle_id; });
    if (runtime != g_runtime_graphs.end()) {
        if (runtime->retirement_pending)
            return;
        if (g_retired_graphs.size() < kMaxRetiredGraphs) {
            g_retired_graphs.push_back(std::move(*runtime));
            g_runtime_graphs.erase(runtime);
        } else {
            runtime->retirement_pending = true;
            LOGI("retired graph queue full; preserving ownership until UnityMain bundle=%u",
                 bundle_id);
        }
        g_retirement_pending_hint.store(1, std::memory_order_release);
        g_retired_graph_count.fetch_add(1, std::memory_order_relaxed);
    }
}

bool set_bundle_presentation_enabled(
    uint32_t bundle_id,
    bool enabled) {
    std::lock_guard<std::mutex> guard(g_graph_lock);
    auto *definition = find_definition_locked(bundle_id);
    if (definition == nullptr)
        return false;
    definition->presentation_enabled = enabled;

    auto runtime = std::find_if(
        g_runtime_graphs.begin(),
        g_runtime_graphs.end(),
        [bundle_id](const GraphRuntime &graph) {
            return graph.bundle_id == bundle_id;
        });
    if (runtime == g_runtime_graphs.end())
        return true;
    if (enabled) {
        // A queue-full retirement has not left g_runtime_graphs yet and can be
        // cancelled. A graph already moved to g_retired_graphs remains retired
        // and will be recreated by the next enabled lifecycle command.
        runtime->retirement_pending = false;
        return true;
    }
    if (!runtime->retirement_pending) {
        if (g_retired_graphs.size() < kMaxRetiredGraphs) {
            g_retired_graphs.push_back(std::move(*runtime));
            g_runtime_graphs.erase(runtime);
        } else {
            runtime->retirement_pending = true;
        }
        g_retirement_pending_hint.store(1, std::memory_order_release);
        g_retired_graph_count.fetch_add(1, std::memory_order_relaxed);
    }
    return true;
}

void clear_bundle_graphs() {
    std::lock_guard<std::mutex> guard(g_graph_lock);
    g_graphs.clear();
    uint32_t retired_count = 0;
    auto runtime = g_runtime_graphs.begin();
    while (runtime != g_runtime_graphs.end()) {
        if (runtime->retirement_pending) {
            ++runtime;
            continue;
        }
        ++retired_count;
        if (g_retired_graphs.size() < kMaxRetiredGraphs) {
            g_retired_graphs.push_back(std::move(*runtime));
            runtime = g_runtime_graphs.erase(runtime);
        } else {
            runtime->retirement_pending = true;
            ++runtime;
        }
    }
    g_retired_graph_count.fetch_add(retired_count, std::memory_order_relaxed);
    if (retired_count != 0)
        g_retirement_pending_hint.store(1, std::memory_order_release);
}

void consume_snapshot(const hud_logic::PresentationSnapshot &snapshot) {
    (void)consume_snapshot_range(
        snapshot,
        0,
        static_cast<uint32_t>(hud_logic::kMaxDuePresentationTasks),
        true);
}

SnapshotRangeConsumeResult consume_snapshot_range(
    const hud_logic::PresentationSnapshot &snapshot,
    uint32_t start_index,
    uint32_t count,
    bool resolve_resources) {
    if (snapshot.available == 0)
        return SnapshotRangeConsumeResult{};

    const uint32_t total = std::min<uint32_t>(
        snapshot.command_count,
        static_cast<uint32_t>(hud_logic::kMaxDuePresentationTasks));
    const uint32_t begin = std::min(start_index, total);
    const uint32_t end = std::min<uint32_t>(
        total,
        begin + std::min<uint32_t>(count, total - begin));
    SnapshotRangeConsumeResult result{};
    size_t materialization_budget = kMaterializationUnityOperationsPerOpportunity;
    for (uint32_t index = begin; index < end; ++index) {
        const auto &command = snapshot.commands[index];
        if (command.session_generation != 0 &&
            snapshot.session_generation != 0 &&
            command.session_generation != snapshot.session_generation) {
            g_invalid_target_count.fetch_add(1, std::memory_order_relaxed);
            ++result.consumed_commands;
            continue;
        }
        if (!apply_presentation_command(command, materialization_budget)) {
            result.deferred = true;
            break;
        }
        ++result.consumed_commands;
    }
    if (resolve_resources && !result.deferred && begin + result.consumed_commands >= total)
        g_resource_resolution_pending.store(1, std::memory_order_release);
    return result;
}

void drain_retired_on_unity_main() {
    drain_retired_graphs();
}

bool has_pending_retirements() {
    return g_retirement_pending_hint.load(std::memory_order_acquire) != 0;
}

void invalidate_all_runtime_graphs_on_unity_main() {
    std::vector<GraphRuntime> invalidated;
    {
        std::lock_guard<std::mutex> guard(g_graph_lock);
        invalidated.swap(g_retired_graphs);
        invalidated.reserve(invalidated.size() + g_runtime_graphs.size());
        for (auto &runtime : g_runtime_graphs)
            invalidated.push_back(std::move(runtime));
        g_runtime_graphs.clear();
    }
    for (auto &graph : invalidated)
        destroy_graph_objects(graph);
}

void set_resource_resolver_callback(void *callback) {
    g_resource_resolver.store(
        reinterpret_cast<ResourceResolverCallback>(callback),
        std::memory_order_release);
    if (callback != nullptr)
        g_resource_resolution_pending.store(1, std::memory_order_release);
}

void drain_pending_resources_on_unity_main() {
    if (g_resource_resolution_pending.exchange(0, std::memory_order_acq_rel) == 0)
        return;
    resolve_pending_resources_on_unity_main();
}

void refresh_resources_on_unity_main() {
    {
        std::lock_guard<std::mutex> guard(g_graph_lock);
        for (auto &runtime : g_runtime_graphs) {
            if (!runtime.materialized || runtime.retirement_pending)
                continue;
            if (runtime.resource_waiting.size() != runtime.resources.size())
                runtime.resource_waiting.assign(runtime.resources.size(), 0);
            else
                std::fill(runtime.resource_waiting.begin(), runtime.resource_waiting.end(), 0);
        }
    }
    g_resource_resolution_pending.store(1, std::memory_order_release);
}

void clear_resources_for_mod_on_unity_main(const std::string &mod_id) {
    if (mod_id.empty())
        return;
    std::lock_guard<std::mutex> guard(g_graph_lock);
    for (auto &runtime : g_runtime_graphs) {
        RegisteredGraph *definition = find_definition_locked(runtime.bundle_id);
        if (definition != nullptr && definition->mod_id == mod_id)
            clear_applied_resources(runtime, *definition);
    }
}

ObjectStats read_stats() {
    std::lock_guard<std::mutex> guard(g_graph_lock);
    uint32_t materialized = 0;
    for (const auto &graph : g_runtime_graphs) {
        if (graph.materialized && !graph.retirement_pending)
            ++materialized;
    }
    return ObjectStats{
        .registered_graphs = static_cast<uint32_t>(g_graphs.size()),
        .materialized_graphs = materialized,
    .materialization_failures = g_materialization_failures.load(std::memory_order_relaxed),
    .invalid_target_count = g_invalid_target_count.load(std::memory_order_relaxed),
    .retired_graphs = g_retired_graph_count.load(std::memory_order_relaxed),
        .unsupported_command_count = g_unsupported_command_count.load(std::memory_order_relaxed),
    };
}

}  // namespace starray::unity_presentation_objects

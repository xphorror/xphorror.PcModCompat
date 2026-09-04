#pragma once

#include "native_rule_vm.h"

#include <cstdint>
#include <string>
#include <vector>

namespace starray::pccompat_recipe {

constexpr uint16_t kSchemaVersion = 1;
constexpr uint32_t kHeaderSize = 96;
constexpr uint32_t kSectionEntrySize = 24;
constexpr uint32_t kSectionCount = 10;

struct RuntimeRule {
    std::string id;
    std::string feature_id;
    std::string source;
    uint32_t stage_code = 0;
    uint32_t op_code = 0;
    uint64_t required_capabilities = 0;
    bool default_enabled = true;
};

struct RuntimeTarget {
    uint32_t id = 0;
    std::string assembly_name;
    std::string namespace_name;
    std::string type_name;
    std::string method_name;
    std::string return_type;
    std::string abi_kind;
    bool is_static = false;
    uint32_t generic_arity = 0;
    std::vector<std::string> parameter_types;
    std::vector<RuntimeRule> rules;
};

enum UiComponentMask : uint32_t {
    UiComponentRectTransform = 1u << 0,
    UiComponentCanvas = 1u << 1,
    UiComponentCanvasScaler = 1u << 2,
    UiComponentImage = 1u << 3,
    UiComponentTextMeshPro = 1u << 4,
    UiComponentCanvasRenderer = 1u << 5,
    UiComponentContentSizeFitter = 1u << 6,
    UiComponentRawImage = 1u << 7,
};

enum UiObjectFlags : uint32_t {
    UiObjectActiveInitially = 1u << 0,
    UiObjectDontDestroyOnLoad = 1u << 1,
};

enum class UiComponentOpCode : uint32_t {
    SetActive = 1,
    SetRect = 2,
    SetAnchors = 3,
    SetPivot = 4,
    SetLocalScale = 5,
    SetCanvasRenderMode = 6,
    SetCanvasSortingOrder = 7,
    SetCanvasScaleMode = 8,
    SetCanvasReferenceResolution = 9,
    SetCanvasMatch = 10,
    SetGraphicColor = 11,
    SetGraphicRaycastTarget = 12,
    SetText = 13,
    SetTextFontSize = 14,
    SetTextAlignment = 15,
    SetTextRichText = 16,
    SetTextLineSpacing = 17,
    SetContentSizeHorizontalFit = 18,
    SetContentSizeVerticalFit = 19,
};

struct UiComponentOperation {
    UiComponentOpCode op_code = UiComponentOpCode::SetActive;
    std::string string_value;
    int64_t payload0 = 0;
    int64_t payload1 = 0;
    int64_t payload2 = 0;
    int64_t payload3 = 0;
};

struct UiObjectNode {
    uint32_t id = 0;
    uint32_t parent_id = 0;
    std::string name;
    uint32_t components = 0;
    uint32_t flags = 0;
    std::vector<UiComponentOperation> initialization;
};

enum class UiResourceTarget : uint32_t {
    ImageSprite = 1,
    RawImageTexture = 2,
    GraphicMaterial = 3,
    TextFont = 4,
    TextFontSharedMaterial = 5,
    TextFontMaterial = 6,
};

struct UiResourceBinding {
    uint32_t node_id = 0;
    UiResourceTarget target = UiResourceTarget::ImageSprite;
    std::string feature_group_id;
    std::string asset_name;
    std::string expected_type;
};

enum class LifecycleTrigger : uint32_t {
    BundleLoad = 1,
    InputSnapshotChanged = 2,
    ClockAnchorChanged = 3,
    OverlayStateChanged = 4,
};

enum class LifecycleClockDomain : uint32_t {
    Realtime = 0,
    UnityScaled = 1,
    Song = 2,
    Audio = 3,
    Map = 4,
};

enum LifecycleFlags : uint32_t {
    LifecycleAllowAnchorExtrapolation = 1u << 0,
    LifecycleRequireInputSnapshot = 1u << 1,
    LifecycleRequireClockAnchor = 1u << 2,
};

struct LifecycleProgram {
    std::string id;
    uint32_t runtime_rule_id = 0;
    LifecycleTrigger trigger = LifecycleTrigger::BundleLoad;
    LifecycleClockDomain clock_domain = LifecycleClockDomain::Realtime;
    uint32_t flags = 0;
    uint32_t program_start = 0;
    uint32_t program_count = 0;
    uint32_t instruction_budget = 0;
    uint32_t command_type = 0;
    uint32_t target_id = 0;
    int64_t initial_delay_ns = 0;
    int64_t deferred_retry_delay_ns = 0;
};

struct ParsedBundle {
    std::string path;
    std::string mod_id;
    std::string recipe_id;
    std::string compatibility;
    uint64_t required_capabilities = 0;
    uint32_t target_game_revision = 0;
    std::array<uint8_t, 32> source_assembly_sha256{};
    std::vector<RuntimeTarget> targets;
    std::vector<UiObjectNode> ui_objects;
    std::vector<UiResourceBinding> ui_resources;
    std::vector<rule_vm::Instruction> bytecode;
    std::vector<LifecycleProgram> lifecycle_programs;
};

bool parse_file(const char *path, ParsedBundle &bundle, std::string &error);

}  // namespace starray::pccompat_recipe

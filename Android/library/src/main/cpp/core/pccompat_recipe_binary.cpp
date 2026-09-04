#include "pccompat_recipe_binary.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <limits>
#include <map>
#include <set>
#include <string_view>
#include <utility>

namespace starray::pccompat_recipe {
namespace {

constexpr std::array<uint8_t, 8> kMagic = {'X', 'P', 'H', 'U', 'I', 'R', 'C', 'P'};
constexpr uint32_t kMaxFileSize = 16u * 1024u * 1024u;
constexpr uint32_t kMaxSections = 32;
constexpr uint32_t kMaxStrings = 1u << 20;
constexpr uint32_t kMaxTargets = 4096;
constexpr uint32_t kMaxRules = 16384;
constexpr uint32_t kMaxParameters = 16384;
constexpr uint32_t kMaxLifecyclePrograms = 4096;
constexpr uint32_t kMaxBytecodeInstructions = 65536;
constexpr uint32_t kMaxUiObjects = 1024;
constexpr uint32_t kMaxComponentOps = 8192;
constexpr uint32_t kMaxResourceBindings = 4096;
constexpr uint32_t kTargetRecordSize = 48;
constexpr uint32_t kRuleRecordSize = 36;
constexpr uint32_t kObjectRecordSize = 32;
constexpr uint32_t kComponentOpRecordSize = 48;
constexpr uint32_t kResourceRecordSize = 32;
constexpr uint32_t kLifecycleRecordSize = 56;
constexpr uint32_t kVmInstructionSize = 16;

enum SectionType : uint32_t {
    SectionStringTable = 1,
    SectionParameterRefs = 2,
    SectionTargets = 3,
    SectionRules = 4,
    SectionObjectGraph = 5,
    SectionComponentOps = 6,
    SectionLifecycle = 7,
    SectionBytecode = 8,
    SectionResources = 9,
    SectionDiagnostics = 10,
};

struct Section {
    uint32_t type = 0;
    uint32_t offset = 0;
    uint32_t size = 0;
    uint32_t count = 0;
    uint32_t element_size = 0;
};

bool range_ok(size_t offset, size_t size, size_t total) {
    return offset <= total && size <= total - offset;
}

std::string_view simple_type_name(std::string_view value) {
    const auto comma = value.find(',');
    if (comma != std::string_view::npos)
        value = value.substr(0, comma);
    while (!value.empty() && value.front() == ' ')
        value.remove_prefix(1);
    while (!value.empty() && value.back() == ' ')
        value.remove_suffix(1);
    const auto dot = value.rfind('.');
    return dot == std::string_view::npos ? value : value.substr(dot + 1);
}

bool equals_ignore_ascii_case(std::string_view left, std::string_view right) {
    if (left.size() != right.size())
        return false;
    for (size_t index = 0; index < left.size(); ++index) {
        auto a = left[index];
        auto b = right[index];
        if (a >= 'A' && a <= 'Z')
            a = static_cast<char>(a - 'A' + 'a');
        if (b >= 'A' && b <= 'Z')
            b = static_cast<char>(b - 'A' + 'a');
        if (a != b)
            return false;
    }
    return true;
}

uint16_t read_u16(const std::vector<uint8_t> &data, size_t offset) {
    return static_cast<uint16_t>(data[offset]) |
           (static_cast<uint16_t>(data[offset + 1]) << 8);
}

uint32_t read_u32(const std::vector<uint8_t> &data, size_t offset) {
    return static_cast<uint32_t>(data[offset]) |
           (static_cast<uint32_t>(data[offset + 1]) << 8) |
           (static_cast<uint32_t>(data[offset + 2]) << 16) |
           (static_cast<uint32_t>(data[offset + 3]) << 24);
}

uint64_t read_u64(const std::vector<uint8_t> &data, size_t offset) {
    return static_cast<uint64_t>(read_u32(data, offset)) |
           (static_cast<uint64_t>(read_u32(data, offset + 4)) << 32);
}

int32_t read_i32(const std::vector<uint8_t> &data, size_t offset) {
    return static_cast<int32_t>(read_u32(data, offset));
}

int64_t read_i64(const std::vector<uint8_t> &data, size_t offset) {
    return static_cast<int64_t>(read_u64(data, offset));
}

uint32_t crc32(const std::vector<uint8_t> &data) {
    constexpr uint32_t polynomial = 0xEDB88320u;
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t index = 0; index < data.size(); ++index) {
        const uint8_t value = index >= 84 && index < 88 ? 0 : data[index];
        crc ^= value;
        for (int bit = 0; bit < 8; ++bit)
            crc = (crc & 1u) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
    }
    return ~crc;
}

bool read_string(const std::vector<uint8_t> &data,
                 const Section &strings,
                 uint32_t relative_offset,
                 std::string &output,
                 std::string &error) {
    if (relative_offset >= strings.size) {
        error = "string offset is outside string table";
        return false;
    }

    const auto begin = static_cast<size_t>(strings.offset) + relative_offset;
    const auto end = static_cast<size_t>(strings.offset) + strings.size;
    const auto terminator = std::find(data.begin() + static_cast<std::ptrdiff_t>(begin),
                                      data.begin() + static_cast<std::ptrdiff_t>(end),
                                      static_cast<uint8_t>(0));
    if (terminator == data.begin() + static_cast<std::ptrdiff_t>(end)) {
        error = "unterminated string in string table";
        return false;
    }

    const auto length = static_cast<size_t>(terminator - (data.begin() + static_cast<std::ptrdiff_t>(begin)));
    if (length > kMaxStrings) {
        error = "string exceeds maximum length";
        return false;
    }

    output.assign(reinterpret_cast<const char *>(data.data() + begin), length);
    return true;
}

const Section *find_section(const std::array<Section, kSectionCount> &sections, uint32_t type) {
    for (const auto &section : sections) {
        if (section.type == type)
            return &section;
    }
    return nullptr;
}

bool validate_section(const Section &section, size_t total, std::string &error) {
    if (section.size == 0) {
        if (section.count != 0 || section.offset != 0 || section.element_size != 0) {
            error = "empty section has non-zero offset/count";
            return false;
        }
        return true;
    }

    if (section.offset < kHeaderSize || !range_ok(section.offset, section.size, total)) {
        error = "section is outside file";
        return false;
    }
    if (section.element_size != 0 &&
        section.count > section.size / section.element_size) {
        error = "section count exceeds section size";
        return false;
    }
    return true;
}

bool parse_header(const std::vector<uint8_t> &data,
                  std::array<Section, kSectionCount> &sections,
                  ParsedBundle &bundle,
                  std::string &error) {
    if (data.size() < kHeaderSize) {
        error = "recipe file is smaller than header";
        return false;
    }
    if (!std::equal(kMagic.begin(), kMagic.end(), data.begin())) {
        error = "invalid ui recipe magic";
        return false;
    }
    if (read_u16(data, 8) != kSchemaVersion) {
        error = "unsupported ui recipe schema version";
        return false;
    }
    if (read_u16(data, 10) != kHeaderSize) {
        error = "invalid ui recipe header size";
        return false;
    }

    const auto flags = read_u32(data, 12);
    if ((flags & 3u) != 3u || (flags & ~31u) != 0 || read_u32(data, 92) != 0) {
        error = "unsupported ui recipe flags or reserved header data";
        return false;
    }

    const auto section_count = read_u32(data, 16);
    const auto total_size = read_u32(data, 80);
    const auto expected_crc = read_u32(data, 84);
    const auto table_offset = read_u32(data, 88);
    if (section_count != kSectionCount || section_count > kMaxSections) {
        error = "unsupported ui recipe section count";
        return false;
    }
    if (total_size != data.size()) {
        error = "ui recipe total size does not match file size";
        return false;
    }
    if (!range_ok(table_offset,
                  static_cast<size_t>(section_count) * kSectionEntrySize,
                  data.size())) {
        error = "ui recipe section table is outside file";
        return false;
    }
    if (expected_crc != crc32(data)) {
        error = "ui recipe checksum mismatch";
        return false;
    }

    bundle.target_game_revision = read_u32(data, 20);
    bundle.required_capabilities = read_u64(data, 24);
    std::copy_n(data.begin() + 48, bundle.source_assembly_sha256.size(),
                bundle.source_assembly_sha256.begin());
    if (std::all_of(bundle.source_assembly_sha256.begin(),
                    bundle.source_assembly_sha256.end(),
                    [](uint8_t value) { return value == 0; })) {
        error = "ui recipe source assembly digest is empty";
        return false;
    }

    const auto table_end = static_cast<size_t>(table_offset) + section_count * kSectionEntrySize;
    std::set<uint32_t> section_types;
    for (uint32_t index = 0; index < section_count; ++index) {
        const auto cursor = static_cast<size_t>(table_offset) + index * kSectionEntrySize;
        sections[index] = Section{
            read_u32(data, cursor),
            read_u32(data, cursor + 4),
            read_u32(data, cursor + 8),
            read_u32(data, cursor + 12),
            read_u32(data, cursor + 16),
        };
        if (read_u32(data, cursor + 20) != 0) {
            error = "ui recipe section has non-zero reserved data";
            return false;
        }
        if (sections[index].type == 0 || sections[index].type > kSectionCount ||
            !section_types.insert(sections[index].type).second) {
            error = "ui recipe contains an unknown or duplicate section type";
            return false;
        }
        if (sections[index].size != 0 && sections[index].offset < table_end) {
            error = "ui recipe section overlaps header or section table";
            return false;
        }
        if (!validate_section(sections[index], data.size(), error))
            return false;
    }

    for (size_t left = 0; left < sections.size(); ++left) {
        if (sections[left].size == 0)
            continue;
        for (size_t right = left + 1; right < sections.size(); ++right) {
            if (sections[right].size == 0)
                continue;
            const auto left_begin = static_cast<uint64_t>(sections[left].offset);
            const auto left_end = left_begin + sections[left].size;
            const auto right_begin = static_cast<uint64_t>(sections[right].offset);
            const auto right_end = right_begin + sections[right].size;
            if (left_begin < right_end && right_begin < left_end) {
                error = "ui recipe sections overlap";
                return false;
            }
        }
    }

    return true;
}

bool parse_tables(const std::vector<uint8_t> &data,
                  const std::array<Section, kSectionCount> &sections,
                  ParsedBundle &bundle,
                  std::string &error) {
    const auto *strings = find_section(sections, SectionStringTable);
    const auto *params = find_section(sections, SectionParameterRefs);
    const auto *targets = find_section(sections, SectionTargets);
    const auto *rules = find_section(sections, SectionRules);
    const auto *object_graph = find_section(sections, SectionObjectGraph);
    const auto *component_ops = find_section(sections, SectionComponentOps);
    const auto *lifecycle = find_section(sections, SectionLifecycle);
    const auto *bytecode = find_section(sections, SectionBytecode);
    const auto *resources = find_section(sections, SectionResources);
    if (strings == nullptr || params == nullptr || targets == nullptr || rules == nullptr ||
        object_graph == nullptr || component_ops == nullptr ||
        lifecycle == nullptr || bytecode == nullptr || resources == nullptr ||
        strings->size == 0 || targets->size == 0 || rules->size == 0) {
        error = "ui recipe is missing required tables";
        return false;
    }
    if (strings->element_size != 1 || strings->count != 1) {
        error = "ui recipe string table descriptor is invalid";
        return false;
    }
    if (targets->element_size != kTargetRecordSize || rules->element_size != kRuleRecordSize ||
        (params->size != 0 && params->element_size != sizeof(uint32_t)) ||
        static_cast<uint64_t>(targets->size) !=
            static_cast<uint64_t>(targets->count) * kTargetRecordSize ||
        static_cast<uint64_t>(rules->size) !=
            static_cast<uint64_t>(rules->count) * kRuleRecordSize ||
        (params->size != 0 &&
         static_cast<uint64_t>(params->size) !=
             static_cast<uint64_t>(params->count) * sizeof(uint32_t))) {
        error = "ui recipe table element size is invalid";
        return false;
    }
    if (targets->count == 0 || targets->count > kMaxTargets ||
        rules->count == 0 || rules->count > kMaxRules ||
        params->count > kMaxParameters) {
        error = "ui recipe table count is outside limits";
        return false;
    }

    if (!read_string(data, *strings, read_u32(data, 32), bundle.mod_id, error) ||
        !read_string(data, *strings, read_u32(data, 36), bundle.recipe_id, error) ||
        !read_string(data, *strings, read_u32(data, 40), bundle.compatibility, error))
        return false;

    std::vector<RuntimeRule> parsed_rules;
    parsed_rules.reserve(rules->count);
    for (uint32_t index = 0; index < rules->count; ++index) {
        const auto cursor = static_cast<size_t>(rules->offset) + index * kRuleRecordSize;
        RuntimeRule rule;
        if (!read_string(data, *strings, read_u32(data, cursor), rule.id, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 4), rule.feature_id, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 8), rule.source, error))
            return false;
        rule.stage_code = read_u32(data, cursor + 12);
        rule.op_code = read_u32(data, cursor + 16);
        if (rule.stage_code > std::numeric_limits<int32_t>::max() ||
            rule.op_code > std::numeric_limits<int32_t>::max()) {
            error = "ui recipe rule code is outside signed range";
            return false;
        }
        rule.required_capabilities = read_u64(data, cursor + 20);
        const auto flags = read_u32(data, cursor + 28);
        if ((flags & ~1u) != 0 || read_u32(data, cursor + 32) != 0) {
            error = "ui recipe rule contains unknown flags";
            return false;
        }
        rule.default_enabled = (flags & 1u) != 0;
        parsed_rules.push_back(std::move(rule));
    }

    const auto parameter_count = params->count;
    std::vector<std::string> parsed_parameters;
    parsed_parameters.reserve(parameter_count);
    for (uint32_t index = 0; index < parameter_count; ++index) {
        const auto cursor = static_cast<size_t>(params->offset) + index * sizeof(uint32_t);
        std::string parameter;
        if (!read_string(data, *strings, read_u32(data, cursor), parameter, error))
            return false;
        parsed_parameters.push_back(std::move(parameter));
    }

    bundle.targets.reserve(targets->count);
    std::set<uint32_t> target_ids;
    for (uint32_t index = 0; index < targets->count; ++index) {
        const auto cursor = static_cast<size_t>(targets->offset) + index * kTargetRecordSize;
        RuntimeTarget target;
        target.id = read_u32(data, cursor);
        const auto parameter_start = read_u32(data, cursor + 28);
        const auto parameter_count_for_target = read_u16(data, cursor + 32);
        const auto flags = read_u16(data, cursor + 34);
        target.generic_arity = read_u32(data, cursor + 36);
        const auto rule_start = read_u32(data, cursor + 40);
        const auto rule_count = read_u32(data, cursor + 44);
        if (target.id == 0 || (flags & ~1u) != 0 ||
            target.generic_arity > std::numeric_limits<int32_t>::max() ||
            !target_ids.insert(target.id).second ||
            parameter_start > parameter_count ||
            parameter_count_for_target > parameter_count - parameter_start ||
            rule_start > rules->count || rule_count > rules->count - rule_start) {
            error = "ui recipe target range or flags are invalid";
            return false;
        }

        if (!read_string(data, *strings, read_u32(data, cursor + 4), target.assembly_name, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 8), target.namespace_name, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 12), target.type_name, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 16), target.method_name, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 20), target.return_type, error) ||
            !read_string(data, *strings, read_u32(data, cursor + 24), target.abi_kind, error))
            return false;

        target.is_static = (flags & 1u) != 0;
        target.parameter_types.insert(
            target.parameter_types.end(),
            parsed_parameters.begin() + parameter_start,
            parsed_parameters.begin() + parameter_start + parameter_count_for_target);
        target.rules.insert(
            target.rules.end(),
            parsed_rules.begin() + rule_start,
            parsed_rules.begin() + rule_start + rule_count);
        if (target.type_name.empty() || target.method_name.empty() || target.return_type.empty() ||
            target.rules.empty()) {
            error = "ui recipe target is incomplete";
            return false;
        }
        bundle.targets.push_back(std::move(target));
    }

    const bool has_object_graph = object_graph->size != 0;
    const bool header_has_object_graph = (read_u32(data, 12) & 8u) != 0;
    if (has_object_graph != header_has_object_graph ||
        (!has_object_graph && component_ops->size != 0)) {
        error = "ui recipe object graph flag/sections disagree";
        return false;
    }
    if (has_object_graph) {
        if (object_graph->element_size != kObjectRecordSize ||
            object_graph->count == 0 || object_graph->count > kMaxUiObjects ||
            static_cast<uint64_t>(object_graph->size) !=
                static_cast<uint64_t>(object_graph->count) * kObjectRecordSize ||
            component_ops->count > kMaxComponentOps ||
            (component_ops->size != 0 &&
             (component_ops->element_size != kComponentOpRecordSize ||
              static_cast<uint64_t>(component_ops->size) !=
                  static_cast<uint64_t>(component_ops->count) * kComponentOpRecordSize))) {
            error = "ui recipe object graph table is invalid";
            return false;
        }

        struct ParsedOperation {
            uint32_t node_id = 0;
            UiComponentOperation operation{};
        };
        std::vector<ParsedOperation> parsed_operations;
        parsed_operations.reserve(component_ops->count);
        for (uint32_t index = 0; index < component_ops->count; ++index) {
            const auto cursor = static_cast<size_t>(component_ops->offset) +
                index * kComponentOpRecordSize;
            ParsedOperation parsed;
            parsed.node_id = read_u32(data, cursor);
            parsed.operation.op_code = static_cast<UiComponentOpCode>(read_u32(data, cursor + 4));
            if (parsed.node_id == 0 ||
                static_cast<uint32_t>(parsed.operation.op_code) <
                    static_cast<uint32_t>(UiComponentOpCode::SetActive) ||
                static_cast<uint32_t>(parsed.operation.op_code) >
                    static_cast<uint32_t>(UiComponentOpCode::SetContentSizeVerticalFit) ||
                read_u32(data, cursor + 12) != 0 ||
                !read_string(
                    data,
                    *strings,
                    read_u32(data, cursor + 8),
                    parsed.operation.string_value,
                    error)) {
                if (error.empty())
                    error = "ui recipe component operation is invalid";
                return false;
            }
            parsed.operation.payload0 = read_i64(data, cursor + 16);
            parsed.operation.payload1 = read_i64(data, cursor + 24);
            parsed.operation.payload2 = read_i64(data, cursor + 32);
            parsed.operation.payload3 = read_i64(data, cursor + 40);
            parsed_operations.push_back(std::move(parsed));
        }

        constexpr uint32_t known_components =
            UiComponentRectTransform |
            UiComponentCanvas |
            UiComponentCanvasScaler |
            UiComponentImage |
            UiComponentTextMeshPro |
            UiComponentCanvasRenderer |
            UiComponentContentSizeFitter |
            UiComponentRawImage;
        constexpr uint32_t known_object_flags =
            UiObjectActiveInitially | UiObjectDontDestroyOnLoad;
        std::vector<uint8_t> operation_ownership(component_ops->count, 0);
        std::map<uint32_t, uint32_t> parents;
        bundle.ui_objects.reserve(object_graph->count);
        for (uint32_t index = 0; index < object_graph->count; ++index) {
            const auto cursor = static_cast<size_t>(object_graph->offset) +
                index * kObjectRecordSize;
            UiObjectNode node;
            node.id = read_u32(data, cursor);
            node.parent_id = read_u32(data, cursor + 4);
            node.components = read_u32(data, cursor + 12);
            node.flags = read_u32(data, cursor + 16);
            const auto operation_start = read_u32(data, cursor + 20);
            const auto operation_count = read_u32(data, cursor + 24);
            if (node.id == 0 || !parents.emplace(node.id, node.parent_id).second ||
                node.parent_id == node.id ||
                (node.components & UiComponentRectTransform) == 0 ||
                (node.components & ~known_components) != 0 ||
                (node.flags & ~known_object_flags) != 0 ||
                operation_start > component_ops->count ||
                operation_count > component_ops->count - operation_start ||
                read_u32(data, cursor + 28) != 0 ||
                !read_string(data, *strings, read_u32(data, cursor + 8), node.name, error) ||
                node.name.empty()) {
                if (error.empty())
                    error = "ui recipe object node is invalid";
                return false;
            }

            node.initialization.reserve(operation_count);
            for (uint32_t op_index = 0; op_index < operation_count; ++op_index) {
                const auto flat_index = operation_start + op_index;
                if (operation_ownership[flat_index] != 0 ||
                    parsed_operations[flat_index].node_id != node.id) {
                    error = "ui recipe component operation ownership is invalid";
                    return false;
                }
                operation_ownership[flat_index] = 1;
                const auto op_code = parsed_operations[flat_index].operation.op_code;
                uint32_t required_components = 0;
                switch (op_code) {
                case UiComponentOpCode::SetRect:
                case UiComponentOpCode::SetAnchors:
                case UiComponentOpCode::SetPivot:
                case UiComponentOpCode::SetLocalScale:
                    required_components = UiComponentRectTransform;
                    break;
                case UiComponentOpCode::SetCanvasRenderMode:
                case UiComponentOpCode::SetCanvasSortingOrder:
                    required_components = UiComponentCanvas;
                    break;
                case UiComponentOpCode::SetCanvasScaleMode:
                case UiComponentOpCode::SetCanvasReferenceResolution:
                case UiComponentOpCode::SetCanvasMatch:
                    required_components = UiComponentCanvasScaler;
                    break;
                case UiComponentOpCode::SetGraphicColor:
                case UiComponentOpCode::SetGraphicRaycastTarget:
                    required_components = UiComponentImage |
                        UiComponentRawImage |
                        UiComponentTextMeshPro;
                    break;
                case UiComponentOpCode::SetText:
                case UiComponentOpCode::SetTextFontSize:
                case UiComponentOpCode::SetTextAlignment:
                case UiComponentOpCode::SetTextRichText:
                case UiComponentOpCode::SetTextLineSpacing:
                    required_components = UiComponentTextMeshPro;
                    break;
                case UiComponentOpCode::SetContentSizeHorizontalFit:
                case UiComponentOpCode::SetContentSizeVerticalFit:
                    required_components = UiComponentContentSizeFitter;
                    break;
                case UiComponentOpCode::SetActive:
                    break;
                }
                if (required_components != 0 &&
                    (node.components & required_components) == 0) {
                    error = "ui recipe component operation has no compatible component";
                    return false;
                }
                node.initialization.push_back(
                    std::move(parsed_operations[flat_index].operation));
            }
            bundle.ui_objects.push_back(std::move(node));
        }
        if (std::find(operation_ownership.begin(), operation_ownership.end(), 0) !=
            operation_ownership.end()) {
            error = "ui recipe contains an unowned component operation";
            return false;
        }
        for (const auto &[id, parent] : parents) {
            if (parent != 0 && parents.find(parent) == parents.end()) {
                error = "ui recipe object parent is missing";
                return false;
            }
            uint32_t cursor = id;
            for (size_t depth = 0; cursor != 0; ++depth) {
                if (depth > parents.size()) {
                    error = "ui recipe object graph contains a cycle";
                    return false;
                }
                cursor = parents.at(cursor);
                if (cursor == id) {
                    error = "ui recipe object graph contains a cycle";
                    return false;
                }
            }
        }
    }

    const bool has_resources = resources->size != 0;
    const bool header_has_resources = (read_u32(data, 12) & 16u) != 0;
    if (has_resources != header_has_resources ||
        (has_resources && !has_object_graph)) {
        error = "ui recipe resource flag/sections disagree";
        return false;
    }
    if (has_resources) {
        if (resources->element_size != kResourceRecordSize ||
            resources->count == 0 || resources->count > kMaxResourceBindings ||
            static_cast<uint64_t>(resources->size) !=
                static_cast<uint64_t>(resources->count) * kResourceRecordSize) {
            error = "ui recipe resource binding table is invalid";
            return false;
        }

        std::map<uint32_t, uint32_t> node_components;
        for (const auto &node : bundle.ui_objects)
            node_components.emplace(node.id, node.components);
        std::set<uint64_t> identities;
        bundle.ui_resources.reserve(resources->count);
        for (uint32_t index = 0; index < resources->count; ++index) {
            const auto cursor = static_cast<size_t>(resources->offset) +
                index * kResourceRecordSize;
            UiResourceBinding binding;
            binding.node_id = read_u32(data, cursor);
            binding.target = static_cast<UiResourceTarget>(read_u32(data, cursor + 4));
            const auto target = static_cast<uint32_t>(binding.target);
            const auto node = node_components.find(binding.node_id);
            const uint64_t identity =
                (static_cast<uint64_t>(binding.node_id) << 32) | target;
            if (binding.node_id == 0 || node == node_components.end() ||
                target < static_cast<uint32_t>(UiResourceTarget::ImageSprite) ||
                target > static_cast<uint32_t>(UiResourceTarget::TextFontMaterial) ||
                !identities.insert(identity).second ||
                read_u32(data, cursor + 20) != 0 ||
                read_u32(data, cursor + 24) != 0 ||
                read_u32(data, cursor + 28) != 0 ||
                !read_string(data, *strings, read_u32(data, cursor + 8),
                    binding.feature_group_id, error) ||
                !read_string(data, *strings, read_u32(data, cursor + 12),
                    binding.asset_name, error) ||
                !read_string(data, *strings, read_u32(data, cursor + 16),
                    binding.expected_type, error) ||
                binding.feature_group_id.empty() || binding.asset_name.empty() ||
                binding.expected_type.empty()) {
                if (error.empty())
                    error = "ui recipe resource binding is invalid";
                return false;
            }

            uint32_t required_components = 0;
            bool type_valid = false;
            const auto expected = simple_type_name(binding.expected_type);
            switch (binding.target) {
            case UiResourceTarget::ImageSprite:
                required_components = UiComponentImage;
                type_valid = equals_ignore_ascii_case(expected, "Sprite");
                break;
            case UiResourceTarget::RawImageTexture:
                required_components = UiComponentRawImage;
                type_valid = equals_ignore_ascii_case(expected, "Texture") ||
                    equals_ignore_ascii_case(expected, "Texture2D");
                break;
            case UiResourceTarget::GraphicMaterial:
                required_components = UiComponentImage |
                    UiComponentRawImage | UiComponentTextMeshPro;
                type_valid = equals_ignore_ascii_case(expected, "Material");
                break;
            case UiResourceTarget::TextFont:
                required_components = UiComponentTextMeshPro;
                type_valid = equals_ignore_ascii_case(expected, "TMP_FontAsset");
                break;
            case UiResourceTarget::TextFontSharedMaterial:
            case UiResourceTarget::TextFontMaterial:
                required_components = UiComponentTextMeshPro;
                type_valid = equals_ignore_ascii_case(expected, "Material");
                break;
            }
            if ((node->second & required_components) == 0 || !type_valid) {
                error = "ui recipe resource binding target/type is incompatible with its node";
                return false;
            }
            bundle.ui_resources.push_back(std::move(binding));
        }
    }

    const bool has_lifecycle = lifecycle->size != 0;
    const bool has_bytecode = bytecode->size != 0;
    const bool header_has_lifecycle = (read_u32(data, 12) & 4u) != 0;
    if (has_lifecycle != has_bytecode || has_lifecycle != header_has_lifecycle) {
        error = "ui recipe lifecycle flag/sections disagree";
        return false;
    }
    if (!has_lifecycle)
        return true;

    if (lifecycle->element_size != kLifecycleRecordSize ||
        bytecode->element_size != kVmInstructionSize ||
        lifecycle->count == 0 || lifecycle->count > kMaxLifecyclePrograms ||
        bytecode->count == 0 || bytecode->count > kMaxBytecodeInstructions ||
        static_cast<uint64_t>(lifecycle->size) !=
            static_cast<uint64_t>(lifecycle->count) * kLifecycleRecordSize ||
        static_cast<uint64_t>(bytecode->size) !=
            static_cast<uint64_t>(bytecode->count) * kVmInstructionSize) {
        error = "ui recipe lifecycle/bytecode table is invalid";
        return false;
    }

    bundle.bytecode.reserve(bytecode->count);
    for (uint32_t index = 0; index < bytecode->count; ++index) {
        const auto cursor = static_cast<size_t>(bytecode->offset) + index * kVmInstructionSize;
        bundle.bytecode.push_back(rule_vm::Instruction{
            .opcode = static_cast<rule_vm::Opcode>(data[cursor]),
            .dst = data[cursor + 1],
            .src0 = data[cursor + 2],
            .src1 = data[cursor + 3],
            .immediate = read_i32(data, cursor + 4),
            .payload = read_i64(data, cursor + 8),
        });
    }

    std::set<std::string> lifecycle_ids;
    std::set<uint32_t> runtime_rule_ids;
    bundle.lifecycle_programs.reserve(lifecycle->count);
    for (uint32_t index = 0; index < lifecycle->count; ++index) {
        const auto cursor = static_cast<size_t>(lifecycle->offset) + index * kLifecycleRecordSize;
        LifecycleProgram program;
        if (!read_string(data, *strings, read_u32(data, cursor), program.id, error))
            return false;
        program.runtime_rule_id = read_u32(data, cursor + 4);
        program.trigger = static_cast<LifecycleTrigger>(read_u32(data, cursor + 8));
        program.clock_domain = static_cast<LifecycleClockDomain>(read_u32(data, cursor + 12));
        program.flags = read_u32(data, cursor + 16);
        program.program_start = read_u32(data, cursor + 20);
        program.program_count = read_u32(data, cursor + 24);
        program.instruction_budget = read_u32(data, cursor + 28);
        program.command_type = read_u32(data, cursor + 32);
        program.target_id = read_u32(data, cursor + 36);
        program.initial_delay_ns = read_i64(data, cursor + 40);
        program.deferred_retry_delay_ns = read_i64(data, cursor + 48);

        const auto trigger = static_cast<uint32_t>(program.trigger);
        const auto domain = static_cast<uint32_t>(program.clock_domain);
        if (program.id.empty() || !lifecycle_ids.insert(program.id).second ||
            program.runtime_rule_id == 0 ||
            !runtime_rule_ids.insert(program.runtime_rule_id).second ||
            trigger < static_cast<uint32_t>(LifecycleTrigger::BundleLoad) ||
            trigger > static_cast<uint32_t>(LifecycleTrigger::OverlayStateChanged) ||
            domain > static_cast<uint32_t>(LifecycleClockDomain::Map) ||
            (program.flags & ~(LifecycleAllowAnchorExtrapolation |
                               LifecycleRequireInputSnapshot |
                               LifecycleRequireClockAnchor)) != 0 ||
            program.program_count == 0 ||
            program.program_start > bytecode->count ||
            program.program_count > bytecode->count - program.program_start ||
            program.instruction_budget == 0 || program.instruction_budget > 1'000'000 ||
            program.command_type == 0 ||
            program.initial_delay_ns < 0 || program.deferred_retry_delay_ns <= 0) {
            error = "ui recipe lifecycle program is invalid";
            return false;
        }

        rule_vm::Exception verification_error{};
        const rule_vm::ProgramView view{
            bundle.bytecode.data() + program.program_start,
            program.program_count,
        };
        if (!rule_vm::verify_program(view, verification_error)) {
            error = "ui recipe lifecycle bytecode verification failed: " +
                std::string(verification_error.message.data());
            return false;
        }
        bundle.lifecycle_programs.push_back(std::move(program));
    }

    return true;
}

}  // namespace

bool parse_file(const char *path, ParsedBundle &bundle, std::string &error) {
    if (path == nullptr || path[0] == '\0') {
        error = "recipe path is empty";
        return false;
    }

    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input) {
        error = "recipe file cannot be opened";
        return false;
    }
    const auto size = input.tellg();
    if (size <= 0 || size > static_cast<std::streamoff>(kMaxFileSize)) {
        error = "recipe file size is outside limits";
        return false;
    }

    std::vector<uint8_t> data(static_cast<size_t>(size));
    input.seekg(0, std::ios::beg);
    if (!input.read(reinterpret_cast<char *>(data.data()), static_cast<std::streamsize>(data.size()))) {
        error = "recipe file read failed";
        return false;
    }

    std::array<Section, kSectionCount> sections{};
    bundle = ParsedBundle{};
    bundle.path = path;
    if (!parse_header(data, sections, bundle, error))
        return false;
    if (!parse_tables(data, sections, bundle, error))
        return false;
    return true;
}

}  // namespace starray::pccompat_recipe

#include "pccompat_recipe_binary.h"

#include <cassert>
#include <cstdio>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

int main(int argc, char **argv) {
    assert(argc == 2 || argc == 3);
    const std::string path = argv[1];
    const bool lifecycle_fixture = argc == 3 && std::string(argv[2]) == "lifecycle";

    starray::pccompat_recipe::ParsedBundle bundle;
    std::string error;
    assert(starray::pccompat_recipe::parse_file(path.c_str(), bundle, error));
    assert(!bundle.mod_id.empty());
    assert(bundle.recipe_id == (lifecycle_fixture
        ? "xphorror.fixture.ui_recipe_vm.v1"
        : "xphorror.recipe.verified_fixed_op.v1"));
    assert(bundle.target_game_revision == 143);
    assert(!bundle.targets.empty());

    size_t rule_count = 0;
    bool found_margin_target = false;
    for (const auto &target : bundle.targets) {
        rule_count += target.rules.size();
        if (target.type_name == "scrMarginTracker" &&
            target.method_name == "CalculatePercentAcc")
            found_margin_target = true;
    }
    if (lifecycle_fixture) {
        assert(rule_count == 1);
        assert(!found_margin_target);
        assert(bundle.lifecycle_programs.size() == 1);
        assert(bundle.bytecode.size() == 3);
        assert(bundle.ui_objects.size() == 2);
        assert(bundle.ui_objects[0].id == 9);
        assert(bundle.ui_objects[0].parent_id == 0);
        assert(bundle.ui_objects[0].initialization.size() == 2);
        assert(bundle.ui_objects[1].id == 10);
        assert(bundle.ui_objects[1].parent_id == 9);
        assert(bundle.ui_objects[1].initialization.size() == 5);
        assert(bundle.ui_objects[1].initialization[0].op_code ==
               starray::pccompat_recipe::UiComponentOpCode::SetText);
        assert(bundle.ui_objects[1].initialization[0].string_value ==
               "UI recipe VM fixture");
        assert(bundle.ui_objects[1].initialization[2].op_code ==
               starray::pccompat_recipe::UiComponentOpCode::SetTextLineSpacing);
        assert(bundle.ui_objects[1].initialization[3].op_code ==
               starray::pccompat_recipe::UiComponentOpCode::SetContentSizeHorizontalFit);
        assert(bundle.ui_objects[1].initialization[4].op_code ==
               starray::pccompat_recipe::UiComponentOpCode::SetContentSizeVerticalFit);
        const auto &program = bundle.lifecycle_programs[0];
        assert(program.id == "fixture.lifecycle");
        assert(program.runtime_rule_id == 1001);
        assert(program.program_start == 0);
        assert(program.program_count == 3);
        assert(program.command_type == 1);
        assert(program.target_id == 9);
        assert(program.instruction_budget == 64);
        assert(bundle.bytecode[0].opcode == starray::rule_vm::Opcode::LoadConstI64);
        assert(bundle.bytecode[0].payload == 42);
        assert(bundle.bytecode[2].opcode == starray::rule_vm::Opcode::Return);
    } else {
        assert(rule_count == 30);
        assert(found_margin_target);
        if (bundle.ui_objects.empty()) {
            // Older cached recipes remain valid during the migration window.
            assert(bundle.lifecycle_programs.empty());
            assert(bundle.bytecode.empty());
        } else {
            assert(bundle.ui_objects.size() >= 1);
            assert(bundle.lifecycle_programs.size() >= 2);
            assert(!bundle.bytecode.empty());
            assert(bundle.ui_objects.front().parent_id == 0);
            bool has_overlay_visibility = false;
            for (const auto &program : bundle.lifecycle_programs) {
                if (program.trigger == starray::pccompat_recipe::LifecycleTrigger::OverlayStateChanged &&
                    program.command_type == 2) {
                    has_overlay_visibility = true;
                    assert(program.program_count == 2);
                    assert(bundle.bytecode[program.program_start].opcode ==
                           starray::rule_vm::Opcode::LoadOverlayVisible);
                }
            }
            assert(has_overlay_visibility);
        }
    }

    std::ifstream input(path, std::ios::binary | std::ios::ate);
    assert(input);
    const auto size = input.tellg();
    assert(size > 0);
    std::vector<uint8_t> corrupted(static_cast<size_t>(size));
    input.seekg(0, std::ios::beg);
    assert(input.read(
        reinterpret_cast<char *>(corrupted.data()),
        static_cast<std::streamsize>(corrupted.size())));
    corrupted.back() ^= 0x5A;

    const auto corrupted_path = path + ".corrupted";
    {
        std::ofstream output(corrupted_path, std::ios::binary | std::ios::trunc);
        assert(output.write(
            reinterpret_cast<const char *>(corrupted.data()),
            static_cast<std::streamsize>(corrupted.size())));
    }

    starray::pccompat_recipe::ParsedBundle rejected;
    error.clear();
    assert(!starray::pccompat_recipe::parse_file(corrupted_path.c_str(), rejected, error));
    assert(error == "ui recipe checksum mismatch");
    std::remove(corrupted_path.c_str());
    return 0;
}

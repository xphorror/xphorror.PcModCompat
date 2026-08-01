# Sets the minimum CMake version required
cmake_minimum_required(VERSION 3.22.1)

# Project name and version
project("starray_modmanager" VERSION 1.0.0 LANGUAGES C CXX)

# ---- Build options ----
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ---- Debug / Release flags ----
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    add_compile_options(-O0 -g -fno-omit-frame-pointer)
else()
    add_compile_options(-O2 -DNDEBUG -ffunction-sections -fdata-sections)
    add_link_options(-Wl,--gc-sections)
endif()

add_compile_options(-Wno-deprecated-declarations)

# ---- Source files ----
set(MODMANAGER_SOURCES
    core/async_input_observer_bridge.cpp
    core/dobby_hook.cpp
    core/hook_broker.cpp
    core/hud_logic_worker.cpp
    core/il2cpp_foreign_thread_guard.cpp
    core/hud_deadline_scheduler.cpp
    core/native_rule_vm.cpp
    core/pccompat_recipe_binary.cpp
    core/pccompat_presentation_abi.cpp
    core/pccompat_hook_rules.cpp
    core/realtime_event_core.cpp
    core/ui_recipe_lifecycle_runtime.cpp
    core/unity_presentation_objects.cpp
    core/unity_presentation_sink.cpp
    core/jni_helper.c
    core/runtime_il2cpp_bridge.c
)

set(MONODROID_SOURCES
    core/mono_droid_coreclr.c
)

# ---- libstarray_modmanager.so (Dobby + metadata resolver + JNI helper + CoreCLR host) ----
add_library(starray_modmanager SHARED
    ${MODMANAGER_SOURCES}
    ${MONODROID_SOURCES}
)

set_target_properties(starray_modmanager PROPERTIES
    C_STANDARD 11
    C_STANDARD_REQUIRED ON
)

# ---- Prebuilt: Dobby (static library) ----
set(STARRAY_DOBBY_LIBRARY "" CACHE FILEPATH "Path to arm64-v8a libdobby.a")
if(STARRAY_DOBBY_LIBRARY STREQUAL "")
    set(STARRAY_DOBBY_LIBRARY "${CMAKE_CURRENT_SOURCE_DIR}/libs/dobby/libdobby.a")
endif()
if(NOT EXISTS "${STARRAY_DOBBY_LIBRARY}")
    message(FATAL_ERROR "Set STARRAY_DOBBY_LIBRARY to an arm64-v8a libdobby.a")
endif()
add_library(dobby STATIC IMPORTED)
set_target_properties(dobby PROPERTIES
    IMPORTED_LOCATION "${STARRAY_DOBBY_LIBRARY}"
)

# ---- cimgui + Dear ImGui backends: linked into the single project SO ----
get_filename_component(STARRAY_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../.." ABSOLUTE)
set(CIMGUI_DIR "${STARRAY_ROOT}/cimgui-1.91.6")
set(IMGUI_DIR "${CIMGUI_DIR}/imgui")

if(NOT EXISTS "${CIMGUI_DIR}/cimgui.cpp")
    set(CIMGUI_DIR "${STARRAY_ROOT}/cimgui")
endif()
if(NOT EXISTS "${IMGUI_DIR}/imgui.cpp")
    set(IMGUI_DIR "${STARRAY_ROOT}/imgui-1.91.6")
endif()
if(NOT EXISTS "${CIMGUI_DIR}/cimgui.cpp")
    set(CIMGUI_DIR "${CMAKE_CURRENT_SOURCE_DIR}/libs/cimgui")
endif()
if(NOT EXISTS "${IMGUI_DIR}/imgui.cpp")
    set(IMGUI_DIR "${CIMGUI_DIR}/imgui")
endif()

if(EXISTS "${CIMGUI_DIR}/cimgui.cpp" AND EXISTS "${IMGUI_DIR}/imgui.cpp")
    target_sources(starray_modmanager PRIVATE
        core/cimgui_compat.cpp
        "${CIMGUI_DIR}/cimgui.cpp"
        "${IMGUI_DIR}/imgui.cpp"
        "${IMGUI_DIR}/imgui_draw.cpp"
        "${IMGUI_DIR}/imgui_demo.cpp"
        "${IMGUI_DIR}/imgui_widgets.cpp"
        "${IMGUI_DIR}/imgui_tables.cpp"
        "${IMGUI_DIR}/backends/imgui_impl_android.cpp"
        "${IMGUI_DIR}/backends/imgui_impl_opengl3.cpp"
    )

    target_compile_definitions(starray_modmanager
        PRIVATE
            IMGUI_DISABLE_OBSOLETE_FUNCTIONS=1
            IMGUI_IMPL_API=extern\ \"C\"
    )
    target_include_directories(starray_modmanager
        PRIVATE
            "${CIMGUI_DIR}"
            "${STARRAY_ROOT}"
            "${IMGUI_DIR}"
            "${IMGUI_DIR}/backends"
    )
    set(STARRAY_HAS_CIMGUI ON)
    message(STATUS "cimgui enabled: ${CIMGUI_DIR}; imgui: ${IMGUI_DIR}")
else()
    set(STARRAY_HAS_CIMGUI OFF)
    message(WARNING "cimgui/imgui sources are missing; building core/native host without ImGui backend symbols")
endif()

# ---- Include directories ----
target_include_directories(starray_modmanager
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/core
        ${CMAKE_CURRENT_SOURCE_DIR}/generated
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${CIMGUI_DIR}
        ${STARRAY_ROOT}
        ${IMGUI_DIR}
        ${IMGUI_DIR}/backends
        ${CMAKE_CURRENT_SOURCE_DIR}/libs/dobby/include
        ${CMAKE_CURRENT_SOURCE_DIR}/libs/coreclr/include
        ${STARRAY_ROOT}/external/AsyncInput/include
)

find_library(log-lib log)

set(STARRAY_MODMANAGER_LIBS
    ${log-lib}
    dobby
    android
    dl
)

if(STARRAY_HAS_CIMGUI)
    list(APPEND STARRAY_MODMANAGER_LIBS EGL GLESv3)
endif()

target_link_libraries(starray_modmanager ${STARRAY_MODMANAGER_LIBS})

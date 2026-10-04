cmake_minimum_required(VERSION 3.24)
project(SnowFlyer2SpectatorUI LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(IMGUI_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/imgui")
set(MINHOOK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/minhook")

add_library(SpectatorUI SHARED
    src/SpectatorUI.cpp
    ${IMGUI_ROOT}/imgui.cpp
    ${IMGUI_ROOT}/imgui_draw.cpp
    ${IMGUI_ROOT}/imgui_tables.cpp
    ${IMGUI_ROOT}/imgui_widgets.cpp
    ${IMGUI_ROOT}/backends/imgui_impl_dx11.cpp
    ${IMGUI_ROOT}/backends/imgui_impl_win32.cpp
    ${MINHOOK_ROOT}/src/buffer.c
    ${MINHOOK_ROOT}/src/hook.c
    ${MINHOOK_ROOT}/src/trampoline.c
    ${MINHOOK_ROOT}/src/hde/hde64.c
)

target_include_directories(SpectatorUI PRIVATE
    src
    ${IMGUI_ROOT}
    ${IMGUI_ROOT}/backends
    ${MINHOOK_ROOT}/include
)

target_compile_definitions(SpectatorUI PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
    IMGUI_DISABLE_OBSOLETE_FUNCTIONS
)

target_compile_options(SpectatorUI PRIVATE /W4 /permissive- /utf-8)
target_link_libraries(SpectatorUI PRIVATE d3d11 dxgi dxguid user32 gdi32 shell32)

set_target_properties(SpectatorUI PROPERTIES
    OUTPUT_NAME "SpectatorUI"
    PREFIX ""
    SUFFIX ".dll"
)


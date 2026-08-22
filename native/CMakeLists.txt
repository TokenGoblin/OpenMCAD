cmake_minimum_required(VERSION 3.25)

# P0-T06 — native build root.
#
# Phase 0 builds a DLL that exports openmcad_version() and nothing else. OCCT is NOT linked yet:
# the manifest pins it (native/vcpkg.json) but the timeboxed OCCT spike required by PLAN.md 14
# has not run, so nothing here should depend on assumptions the spike has not verified.
#
# Turn OPENMCAD_WITH_OCCT on in Phase 1 (P1-T06), once the spike has confirmed the version,
# the build flags, and the behaviour we are about to design around.

project(openmcad_native
    VERSION 0.1.0
    DESCRIPTION "OpenMCAD native shims"
    LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Determinism is a hard requirement (ADR-0011, PLAN.md 5.2.3), and it starts with a reproducible
# native build. Anything added here that varies per machine will surface later as a corpus diff
# that takes a week to track down.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    add_compile_options(
        /W4         # high warning level
        /WX         # warnings are errors, matching the managed side
        /permissive-
        /EHsc       # standard C++ exception handling; the C ABI boundary catches everything
        /utf-8
        /Zc:__cplusplus
        /Zc:preprocessor
        /fp:precise # never /fp:fast — geometry depends on IEEE semantics
    )
    add_compile_definitions(NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
else()
    add_compile_options(-Wall -Wextra -Werror -ffp-contract=off)
endif()

option(OPENMCAD_WITH_OCCT "Link the OCCT geometry kernel (Phase 1, P1-T06)" OFF)
option(OPENMCAD_WITH_PLANEGCS "Link the planegcs constraint solver (Phase 4, P4-T01)" OFF)

add_subdirectory(openmcad_occt)

if(OPENMCAD_WITH_PLANEGCS)
    # TODO(P4-T01): add_subdirectory(openmcad_gcs)
    message(STATUS "openmcad_gcs is not implemented yet (P4-T01)")
endif()

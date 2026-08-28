cmake_minimum_required(VERSION 3.25)

# file(GET_RUNTIME_DEPENDENCIES) normalises paths before matching the exclude regexes. Without
# this the behaviour depends on the CMake version, and the system32 exclusions in
# openmcad_occt/CMakeLists.txt are written against the normalised form.
if(POLICY CMP0207)
    cmake_policy(SET CMP0207 NEW)
endif()

# P0-T06 — native build root.
#
# Phase 0 builds a DLL that exports openmcad_version() and nothing else. OCCT is NOT linked yet:
# the manifest pins it (native/vcpkg.json) but the timeboxed OCCT spike required by PLAN.md 14
# has not run, so nothing here should depend on assumptions the spike has not verified.
#
# OPENMCAD_WITH_OCCT is still OFF, and P1-T06 is done. The spike confirmed the version and the
# flags (docs/notes/occt-spike.md) and both kernels are runnable, so what remains is the decision
# to switch it on and absorb a cold build measured in hours -- see docs/notes/open-decisions.md.
# Nothing in Phase 5 can produce real geometry until it is on.

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
        /utf-8
        /Zc:__cplusplus
        /Zc:preprocessor
        /fp:precise # never /fp:fast — geometry depends on IEEE semantics
    )

    # /EHa, not /EHsc, and this is P1-T05 rather than a preference.
    #
    # OSD::SetSignal installs a structured-exception translator so that an access violation or a
    # division by zero raised deep inside OCCT arrives at our firewall as a catchable
    # Standard_Failure. MSVC only runs that translator under /EHa. Under /EHsc the translator is
    # never invoked, the fault propagates as a raw SEH exception, and the process dies -- which is
    # exactly what happened here: a fillet with an impossible radius took the whole test host down
    # with 0xC0000005 despite SetSignal being installed and the guard being in place.
    #
    # The cost is that the compiler must assume any instruction can throw, so it optimises less
    # aggressively around calls. That is the correct trade: a user losing their session to a bad
    # fillet radius is not a performance problem.
    #
    # CMake puts /EHsc in CMAKE_CXX_FLAGS by default, and the two are contradictory, so the
    # default is rewritten rather than appended to.
    string(REGEX REPLACE "/EH[a-z]+" "" CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS}")
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} /EHa")
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

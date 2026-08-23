# P0-T06 — the OCCT shim skeleton.
#
# Produces openmcad_occt.dll exporting a single function. Everything about the boundary design
# (ADR-0003) is already reflected here even though there is only one export: extern "C", noexcept,
# integer status returns, no pointers crossing, caller-owned buffers.

# Generated from native/kernel.api.json by tools/idlgen. Checked in rather than generated during
# the build, so CMake never needs the .NET SDK -- a dependency nobody wants in a C++ toolchain.
# CI runs `idlgen --check` so the checked-in files cannot drift from the IDL.
set(OPENMCAD_GENERATED_SOURCES
    src/openmcad_dispatch.g.cpp
)

add_library(openmcad_occt SHARED
    src/openmcad_occt.cpp
    ${OPENMCAD_GENERATED_SOURCES}
)

if(OPENMCAD_WITH_OCCT)
    # The real operation bodies. One file per group, hand-written; see docs/specs/kernel-shim.md.
    file(GLOB OPENMCAD_OP_SOURCES CONFIGURE_DEPENDS "src/ops/*.cpp")
    if(NOT OPENMCAD_OP_SOURCES)
        message(FATAL_ERROR
            "OPENMCAD_WITH_OCCT is on but native/openmcad_occt/src/ops is empty. "
            "The operation bodies land at P1-T06.")
    endif()

    # The handle table and canonical ordering need OCCT types, so they join the OCCT build rather
    # than the stub one. The stub build stays compilable without vcpkg, which is what lets CI
    # verify the whole ABI surface on a machine that has no kernel.
    target_sources(openmcad_occt PRIVATE
        src/openmcad_handles.cpp
        src/openmcad_canonical.cpp
        ${OPENMCAD_OP_SOURCES})
else()
    # Not-implemented fallbacks, so the shim links and exports its whole ABI before any geometry
    # exists. The boundary, the handle table, the firewall and the C# bindings can then all be
    # exercised against a real DLL while the kernel work is still ahead.
    target_sources(openmcad_occt PRIVATE src/openmcad_stubs.g.cpp)
endif()

target_include_directories(openmcad_occt
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
)

target_compile_definitions(openmcad_occt
    PRIVATE
        OPENMCAD_OCCT_EXPORTS
        OPENMCAD_VERSION_STRING="${PROJECT_VERSION}"
)

set_target_properties(openmcad_occt PROPERTIES
    OUTPUT_NAME "openmcad_occt"
    # No "lib" prefix on any platform: the C# [LibraryImport] name must be stable and identical
    # everywhere, and DllImport resolution does not want to guess.
    PREFIX ""
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

if(OPENMCAD_WITH_OCCT)
    # TODO(P1-T06): find_package(OpenCASCADE CONFIG REQUIRED) and link the modules actually used.
    # Link the narrowest set that works. OCCT is large, and pulling in modules "just in case"
    # inflates the shipped payload and the surface that has to be licence-audited (PLAN.md 8.6).
    find_package(OpenCASCADE CONFIG REQUIRED)
    message(STATUS "OCCT ${OpenCASCADE_VERSION}, TBB=${OpenCASCADE_WITH_TBB}, shared=${OpenCASCADE_BUILD_SHARED_LIBS}")

    if(OpenCASCADE_WITH_TBB)
        # PLAN.md 5.2.3 and ADR-0011. TBB introduces scheduling-dependent parallelism inside OCCT,
        # and the determinism gate rests on results not varying between runs. If this ever needs to
        # be turned on, it comes with a double-rebuild diff proving it changed nothing.
        message(FATAL_ERROR
            "OCCT was built with Intel TBB. Determinism requires it off; see native/vcpkg.json.")
    endif()
    target_link_libraries(openmcad_occt PRIVATE
        TKernel
        TKMath
        TKG2d
        TKG3d
        TKGeomBase
        TKBRep
        TKGeomAlgo
        TKTopAlgo
        TKPrim
        TKBO
        TKFillet
        TKMesh
        TKShHealing
        TKDESTEP
    )
    target_compile_definitions(openmcad_occt PRIVATE OPENMCAD_WITH_OCCT=1)
endif()

install(TARGETS openmcad_occt
        RUNTIME_DEPENDENCY_SET openmcad_runtime
        RUNTIME DESTINATION bin
        LIBRARY DESTINATION lib)

if(OPENMCAD_WITH_OCCT)
    # The shim is useless on its own: it needs the OCCT libraries it linked against, and those
    # need zlib, freetype and the rest. Resolving the closure rather than copying every DLL vcpkg
    # produced is the difference between shipping what is used and shipping what was available --
    # and a wrong guess here fails at load time on a machine that is not this one.
    install(RUNTIME_DEPENDENCY_SET openmcad_runtime
            DESTINATION bin
            DIRECTORIES "$<TARGET_FILE_DIR:TKernel>"

            # The Windows API sets and anything already in system32 belong to the operating
            # system. Copying them alongside the application is at best redundant and at worst a
            # version conflict with the host.
            PRE_EXCLUDE_REGEXES
                "api-ms-win-.*"
                "ext-ms-.*"
                "hvsifiletrust.dll"
                "pdmutilities.dll"
            # CMake normalises these paths to forward slashes before matching, so a pattern
            # written with backslashes silently matches nothing -- which is how kernel32 and
            # user32 ended up in the first closure.
            POST_EXCLUDE_REGEXES
                ".*[Ss]ystem32/.*\\.dll"
                ".*[Ww]inSxS/.*\\.dll")
endif()

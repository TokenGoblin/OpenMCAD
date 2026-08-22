/*
 * openmcad_occt.cpp - the hand-written core of the shim.
 *
 * Everything here is what the generator cannot produce: the thread-local diagnostic record, the
 * null-argument failure path, and the four operations that must work in every build regardless of
 * whether a geometry kernel is linked.
 *
 * The exported entry points themselves are generated (openmcad_dispatch.g.cpp). Nothing in this
 * file is an export.
 */

#include "openmcad_types.h"

#include <string>
#include <vector>

#if defined(OPENMCAD_WITH_OCCT)
#  include <OSD.hxx>
#  include <Standard_Version.hxx>
#endif

namespace openmcad {

namespace {

/*
 * The last-error record, per thread.
 *
 * Thread-local rather than global because the dispatcher (ADR-0004) is single-threaded today but
 * the Phase 15 worker pool (P15-T03) is not, and a shared error slot would become an
 * unreproducible data race exactly when the system is under most load.
 */
thread_local std::string g_last_error;

std::string build_version()
{
    std::string version = OPENMCAD_VERSION_STRING;

#if defined(OPENMCAD_WITH_OCCT)
    version.append(" (OCCT ");
    version.append(OCC_VERSION_COMPLETE);
    version.append(")");
#else
    version.append(" (no kernel linked)");
#endif

    return version;
}

} /* namespace */

void record_error(const char* operation, const char* detail) noexcept
{
    try
    {
        g_last_error.assign(operation != nullptr ? operation : "(unknown operation)");
        g_last_error.append(": ");
        g_last_error.append(detail != nullptr ? detail : "(no detail)");
    }
    catch (...)
    {
        /*
         * Recording a diagnostic must never itself fail the call. If even this allocation fails,
         * the process is in real trouble and the status code alone will have to do.
         */
        g_last_error.clear();
    }
}

OpenMcadStatus fail_null(const char* operation, const char* parameter) noexcept
{
    try
    {
        // Not "output parameter": the generated guards also cover input pointers such as a
        // transform or a vector, and telling someone their input is an output wastes their time.
        std::string detail = "the required pointer parameter '";
        detail.append(parameter != nullptr ? parameter : "?");
        detail.append("' was null");
        record_error(operation, detail.c_str());
    }
    catch (...)
    {
        record_error(operation, "a required pointer parameter was null");
    }

    return OPENMCAD_ERROR_INVALID_INPUT;
}

namespace ops {

void initialize()
{
#if defined(OPENMCAD_WITH_OCCT)
    /*
     * P1-T05, and load-bearing. Without this an FPE raised inside OCCT terminates the process
     * instead of arriving at the firewall as a catchable Standard_Failure -- so a user loses their
     * session to a modelling operation that should have reported "this fillet is impossible".
     */
    OSD::SetSignal(Standard_False);
#endif

    g_last_error.clear();
}

void shutdown()
{
    g_last_error.clear();
    g_last_error.shrink_to_fit();
}

void version(openmcad::OutBuffer<char> text)
{
    static const std::string value = build_version();
    write_utf8(text, value);
}

void last_error(openmcad::OutBuffer<char> text)
{
    write_utf8(text, g_last_error);
}

} /* namespace ops */

} /* namespace openmcad */

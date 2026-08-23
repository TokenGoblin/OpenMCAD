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

#  include "openmcad_handles.h"
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

/*
 * Diagnostics from the operation that just ran, per thread.
 *
 * Separate from the last-error string: that is one line of prose for a log, whereas these carry a
 * stable code, a severity, and the entities at fault, and the managed side turns them into
 * KernelDiagnostic objects. An operation may report several -- a fillet that failed on three of
 * twelve edges has three things to say.
 *
 * Cleared at the start of each operation by the managed layer calling diagnostics_clear, not
 * automatically: a caller that wants to accumulate across a retry ladder needs them to persist.
 */
struct Diagnostic
{
    int32_t severity = 2;
    std::string code;
    std::string message;
    std::vector<uint64_t> entities;
};

thread_local std::vector<Diagnostic> g_diagnostics;

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

namespace {

const Diagnostic& at(int32_t index)
{
    if (index < 0 || static_cast<size_t>(index) >= g_diagnostics.size())
    {
        throw invalid_input(
            "Diagnostic index " + std::to_string(index) + " is out of range; there are "
            + std::to_string(g_diagnostics.size()) + ".");
    }

    return g_diagnostics[static_cast<size_t>(index)];
}

} /* namespace */

void report(int32_t severity, const char* code, const std::string& message,
            const std::vector<uint64_t>& entities)
{
    try
    {
        g_diagnostics.push_back(Diagnostic{severity, code, message, entities});
    }
    catch (...)
    {
        /* Losing a diagnostic must not fail the operation that produced it. */
    }
}

namespace ops {

void initialize()
{
    g_diagnostics.clear();

#if defined(OPENMCAD_WITH_OCCT)
    /*
     * P1-T05, and load-bearing. Without this an FPE raised inside OCCT terminates the process
     * instead of arriving at the firewall as a catchable Standard_Failure -- so a user loses their
     * session to a modelling operation that should have reported "this fillet is impossible".
     */
    OSD::SetSignal(false);
#endif

    g_last_error.clear();
}

void shutdown()
{
    g_last_error.clear();
    g_last_error.shrink_to_fit();
    g_diagnostics.clear();
    g_diagnostics.shrink_to_fit();

#if defined(OPENMCAD_WITH_OCCT)
    // Release every live shape. A non-zero count here at shutdown is a leak, and the count is
    // what the leak test asserts on.
    handles().clear();
#endif
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

void diagnostic_count(int32_t& count)
{
    count = static_cast<int32_t>(g_diagnostics.size());
}

void diagnostic_severity(int32_t index, int32_t& severity)
{
    severity = at(index).severity;
}

void diagnostic_code(int32_t index, openmcad::OutBuffer<char> code)
{
    write_utf8(code, at(index).code);
}

void diagnostic_message(int32_t index, openmcad::OutBuffer<char> message)
{
    write_utf8(message, at(index).message);
}

void diagnostic_entities(int32_t index, openmcad::OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t>& tags = at(index).entities;
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

void diagnostics_clear()
{
    g_diagnostics.clear();
}

} /* namespace ops */

} /* namespace openmcad */

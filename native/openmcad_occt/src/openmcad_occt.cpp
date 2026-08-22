/*
 * openmcad_occt.cpp — the C ABI shim over OCCT.
 *
 * P0-T06 skeleton. Only openmcad_version, openmcad_last_error, openmcad_initialize and
 * openmcad_shutdown exist. The exception firewall and the two-call buffer pattern are already in
 * their final form, because those are the parts every later operation copies, and a bad pattern
 * established here would be replicated three hundred times.
 *
 * Operation bodies are hand-written from P1-T06; the dispatch around them is generated.
 */

#include "openmcad_occt.h"

#include <cstring>
#include <exception>
#include <string>

#if defined(OPENMCAD_WITH_OCCT)
#  include <Standard_Failure.hxx>
#  include <Standard_Version.hxx>
#  include <OSD.hxx>
#endif

namespace
{

/*
 * The last-error record, per thread.
 *
 * Thread-local rather than global because the kernel dispatcher (ADR-0004) is single-threaded
 * today but the Phase 15 worker pool (P15-T03) is not, and a shared error slot would become an
 * unreproducible data race exactly when the system is under the most load.
 */
thread_local std::string g_last_error;

void set_last_error(const char* operation, const char* detail) noexcept
{
    try
    {
        g_last_error.assign(operation);
        g_last_error.append(": ");
        g_last_error.append(detail != nullptr ? detail : "(no detail)");
    }
    catch (...)
    {
        // Recording a diagnostic must never itself fail the call. If even this allocation fails
        // the process is in real trouble and the status code alone will have to do.
        g_last_error.clear();
    }
}

/*
 * Copies a NUL-terminated string out using the two-call pattern.
 *
 * Passing buffer == nullptr reports the required size, including the terminator, and returns OK.
 * A buffer that is present but too small reports the required size and returns
 * OPENMCAD_ERROR_BUFFER_TOO_SMALL, so a caller that guessed can retry without a second query.
 */
OpenMcadStatus copy_out(
    const std::string& value,
    char* buffer,
    int32_t buffer_size,
    int32_t* required_size) noexcept
{
    const int32_t needed = static_cast<int32_t>(value.size()) + 1;

    if (required_size != nullptr)
    {
        *required_size = needed;
    }

    if (buffer == nullptr)
    {
        return OPENMCAD_OK;
    }

    if (buffer_size < needed)
    {
        return OPENMCAD_ERROR_BUFFER_TOO_SMALL;
    }

    std::memcpy(buffer, value.c_str(), static_cast<size_t>(needed));
    return OPENMCAD_OK;
}

std::string build_version_string()
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

} // namespace

/*
 * OPENMCAD_GUARD — the exception firewall.
 *
 * Every export wraps its body in this. OCCT throws Standard_Failure, the standard library throws
 * std::exception, and native code can throw things that are neither; all three are caught here
 * and converted to a status plus a thread-local diagnostic. Nothing propagates across the ABI.
 */
#if defined(OPENMCAD_WITH_OCCT)
#  define OPENMCAD_CATCH_KERNEL(op)                                     \
    catch (const Standard_Failure& failure)                             \
    {                                                                   \
        set_last_error(op, failure.GetMessageString());                 \
        return OPENMCAD_ERROR_KERNEL_FAILURE;                           \
    }
#else
#  define OPENMCAD_CATCH_KERNEL(op)
#endif

#define OPENMCAD_GUARD(op, body)                                        \
    try                                                                 \
    {                                                                   \
        body                                                            \
    }                                                                   \
    OPENMCAD_CATCH_KERNEL(op)                                           \
    catch (const std::bad_alloc&)                                       \
    {                                                                   \
        set_last_error(op, "out of memory");                            \
        return OPENMCAD_ERROR_OUT_OF_MEMORY;                            \
    }                                                                   \
    catch (const std::exception& error)                                 \
    {                                                                   \
        set_last_error(op, error.what());                               \
        return OPENMCAD_ERROR_INTERNAL;                                 \
    }                                                                   \
    catch (...)                                                         \
    {                                                                   \
        set_last_error(op, "unknown native exception");                 \
        return OPENMCAD_ERROR_INTERNAL;                                 \
    }

extern "C" {

OPENMCAD_API OpenMcadStatus OPENMCAD_CALL openmcad_version(
    char* buffer,
    int32_t buffer_size,
    int32_t* required_size)
{
    OPENMCAD_GUARD("openmcad_version",
    {
        static const std::string version = build_version_string();
        return copy_out(version, buffer, buffer_size, required_size);
    })
}

OPENMCAD_API OpenMcadStatus OPENMCAD_CALL openmcad_last_error(
    char* buffer,
    int32_t buffer_size,
    int32_t* required_size)
{
    OPENMCAD_GUARD("openmcad_last_error",
    {
        return copy_out(g_last_error, buffer, buffer_size, required_size);
    })
}

OPENMCAD_API OpenMcadStatus OPENMCAD_CALL openmcad_initialize(void)
{
    OPENMCAD_GUARD("openmcad_initialize",
    {
#if defined(OPENMCAD_WITH_OCCT)
        // P1-T05. Without this, an FPE inside OCCT takes the process down instead of arriving
        // here as a catchable Standard_Failure.
        OSD::SetSignal(Standard_False);
#endif
        g_last_error.clear();
        return OPENMCAD_OK;
    })
}

OPENMCAD_API OpenMcadStatus OPENMCAD_CALL openmcad_shutdown(void)
{
    OPENMCAD_GUARD("openmcad_shutdown",
    {
        g_last_error.clear();
        g_last_error.shrink_to_fit();
        return OPENMCAD_OK;
    })
}

} // extern "C"

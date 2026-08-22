/*
 * openmcad_occt.h — the C ABI over the OCCT geometry kernel.
 *
 * P0-T06 ships the skeleton. From P1-T03 onward this header is GENERATED from
 * native/kernel.api.json by tools/idlgen, and hand-editing it will be overwritten. The handful of
 * declarations below are the fixed preamble the generator emits around the generated operations.
 *
 * Boundary rules (ADR-0003) — every one of these is load-bearing, not stylistic:
 *
 *   - Every export is extern "C" and noexcept, and returns an OpenMcadStatus.
 *     OCCT throws Standard_Failure. A C++ exception crossing this boundary is undefined
 *     behaviour, so every entry point catches everything.
 *
 *   - No pointers to kernel-owned memory ever cross. Shapes are opaque uint64 handles into a
 *     shim-side table, with a generation counter in the high bits so a stale handle is detected
 *     rather than aliasing a recycled slot. This is also, deliberately, how Parasolid works, which
 *     keeps ADR-0002's swap path cheap.
 *
 *   - Bulk data uses the two-call pattern: ask for the size, then supply a buffer. The alternative
 *     (returning a shim-allocated pointer) needs a matching free on every path including the error
 *     paths, and that is how boundaries leak.
 *
 *   - Diagnostics are retrieved out-of-band via openmcad_last_error, from a thread-local record.
 *     Returning a status code alone would discard the OCCT exception text, which is the single
 *     most useful thing in a kernel bug report.
 */

#ifndef OPENMCAD_OCCT_H
#define OPENMCAD_OCCT_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(OPENMCAD_OCCT_EXPORTS)
#    define OPENMCAD_API __declspec(dllexport)
#  else
#    define OPENMCAD_API __declspec(dllimport)
#  endif
#  define OPENMCAD_CALL __cdecl
#else
#  define OPENMCAD_API __attribute__((visibility("default")))
#  define OPENMCAD_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Status codes. Zero is success; everything else is a failure the caller must handle.
 * Values are part of the ABI: append only, never renumber.
 */
typedef enum OpenMcadStatus
{
    OPENMCAD_OK = 0,

    /* The call was well formed but the operation did not succeed. */
    OPENMCAD_ERROR_KERNEL_FAILURE = 1,   /* OCCT reported a failure                          */
    OPENMCAD_ERROR_INVALID_INPUT = 2,    /* arguments failed validation before the kernel     */
    OPENMCAD_ERROR_INVALID_HANDLE = 3,   /* unknown or stale handle (generation mismatch)     */
    OPENMCAD_ERROR_BUFFER_TOO_SMALL = 4, /* two-call pattern: retry with the reported size    */
    OPENMCAD_ERROR_OUT_OF_MEMORY = 5,
    OPENMCAD_ERROR_NOT_IMPLEMENTED = 6,
    OPENMCAD_ERROR_CANCELLED = 7,        /* a superseded rebuild cancelled at an op boundary  */

    /* The shim itself is in a bad state. Treat as fatal and capture a repro bundle. */
    OPENMCAD_ERROR_INTERNAL = 100
} OpenMcadStatus;

/*
 * An opaque handle to a kernel object. Zero is never valid.
 * Low bits index the handle table; high bits are a generation counter.
 */
typedef uint64_t OpenMcadHandle;

#define OPENMCAD_INVALID_HANDLE ((OpenMcadHandle)0)

/*
 * The operations themselves are declared in the generated openmcad_occt.g.h, which includes this
 * header for the status codes, handle type, and export macros above. This file is the fixed
 * preamble; that file is the surface.
 */

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* OPENMCAD_OCCT_H */

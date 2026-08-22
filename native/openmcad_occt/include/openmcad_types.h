/*
 * openmcad_types.h - the C++ vocabulary the generated dispatch layer speaks.
 *
 * Hand-written, and deliberately small. The generator emits calls in terms of these types, so the
 * generator itself never has to know that OCCT exists (ADR-0002). Swapping kernels replaces the
 * operation bodies and leaves the generator, the IDL, and this header untouched.
 *
 * Nothing here allocates on behalf of a caller, and nothing hands out a pointer to kernel-owned
 * memory. Both are ADR-0003 rules, and both are enforced by the shapes of these types rather than
 * by remembering.
 */

#ifndef OPENMCAD_TYPES_H
#define OPENMCAD_TYPES_H

#include <cstring>
#include <span>
#include <new>
#include <stdexcept>
#include <stdint.h>
#include <string>

#include "openmcad_occt.h"

namespace openmcad {

/* --- handles -----------------------------------------------------------------------------
 *
 * Distinct types rather than bare uint64_t. They cost nothing at runtime and they make it a
 * compile error to pass a mesh where a shape belongs -- which, with every handle being the same
 * 64-bit integer, is otherwise a mistake nothing would catch.
 */

struct ShapeRef   { uint64_t tag; };
struct EntityRef  { uint64_t tag; };
struct HistoryRef { uint64_t tag; };
struct MeshRef    { uint64_t tag; };

/* Output handle slots. Assigning to one writes through to the caller. */
struct ShapeOut
{
    uint64_t* slot;
    void set(ShapeRef value) const noexcept { *slot = value.tag; }
};

struct HistoryOut
{
    uint64_t* slot;
    void set(HistoryRef value) const noexcept { *slot = value.tag; }
};

struct MeshOut
{
    uint64_t* slot;
    void set(MeshRef value) const noexcept { *slot = value.tag; }
};

/* --- fixed-size value blocks -------------------------------------------------------------- */

struct Vec2
{
    double x;
    double y;
};

struct Vec3
{
    double x;
    double y;
    double z;

    static Vec3 from(const double* values) noexcept
    {
        return Vec3{values[0], values[1], values[2]};
    }
};

/*
 * A similarity transform, laid out as the eight doubles the managed side sends:
 * quaternion xyzw, translation xyz, uniform scale.
 *
 * The layout is part of the ABI. It matches OpenMCAD.Math.Transform field for field, so the
 * managed side can write it without a conversion step.
 */
struct Transform
{
    double qx, qy, qz, qw;
    double tx, ty, tz;
    double scale;

    static Transform from(const double* values) noexcept
    {
        Transform result{};
        std::memcpy(&result, values, sizeof(Transform));
        return result;
    }
};

/* --- spans in ------------------------------------------------------------------------------- */

template <typename T>
inline std::span<const T> make_span(const T* data, int32_t count) noexcept
{
    if (data == nullptr || count <= 0)
    {
        return std::span<const T>{};
    }

    return std::span<const T>{data, static_cast<size_t>(count)};
}

inline std::span<const Vec2> make_vec2_span(const double* data, int32_t count) noexcept
{
    if (data == nullptr || count <= 0)
    {
        return std::span<const Vec2>{};
    }

    /* The caller sends interleaved xy pairs; count is the number of points, not of doubles. */
    return std::span<const Vec2>{reinterpret_cast<const Vec2*>(data), static_cast<size_t>(count)};
}

/* --- buffers out: the two-call pattern ---------------------------------------------------------
 *
 * A caller passes a null buffer to ask for the required size, then calls again with one that
 * large. write() implements both halves, so no operation body has to remember the protocol -- and
 * more importantly, none of them can implement it slightly differently.
 */

template <typename T>
struct OutBuffer
{
    T* data;
    int32_t capacity;
    int32_t* required;

    /* Reports the size and copies if there is room. Returns false when the caller must retry. */
    bool write(std::span<const T> values) const noexcept
    {
        const int32_t needed = static_cast<int32_t>(values.size());

        if (required != nullptr)
        {
            *required = needed;
        }

        if (data == nullptr)
        {
            return false; /* Size query only. */
        }

        if (capacity < needed)
        {
            return false; /* Caller retries with a larger buffer. */
        }

        if (needed > 0)
        {
            std::memcpy(data, values.data(), static_cast<size_t>(needed) * sizeof(T));
        }

        return true;
    }
};

/* Convenience for the common case of a NUL-terminated string. */
inline bool write_utf8(const OutBuffer<char>& out, const std::string& text) noexcept
{
    const int32_t needed = static_cast<int32_t>(text.size()) + 1;

    if (out.required != nullptr)
    {
        *out.required = needed;
    }

    if (out.data == nullptr || out.capacity < needed)
    {
        return false;
    }

    std::memcpy(out.data, text.c_str(), static_cast<size_t>(needed));
    return true;
}

/* --- failures ------------------------------------------------------------------------------------
 *
 * Operation bodies signal failure by throwing. The generated dispatch catches everything and
 * converts it, so a body never has to thread a status code through its own control flow -- which
 * is what makes writing one a matter of geometry rather than of bookkeeping.
 */

class kernel_error : public std::runtime_error
{
public:
    kernel_error(OpenMcadStatus status, const std::string& message)
        : std::runtime_error(message), status_(status)
    {
    }

    OpenMcadStatus status() const noexcept { return status_; }

private:
    OpenMcadStatus status_;
};

class not_implemented : public kernel_error
{
public:
    explicit not_implemented(const std::string& operation)
        : kernel_error(
              OPENMCAD_ERROR_NOT_IMPLEMENTED,
              "The operation '" + operation + "' is declared in the IDL but has no implementation "
              "in this build. Link with OPENMCAD_WITH_OCCT to get the real one.")
    {
    }
};

class invalid_input : public kernel_error
{
public:
    explicit invalid_input(const std::string& message)
        : kernel_error(OPENMCAD_ERROR_INVALID_INPUT, message)
    {
    }
};

class invalid_handle : public kernel_error
{
public:
    explicit invalid_handle(uint64_t tag)
        : kernel_error(
              OPENMCAD_ERROR_INVALID_HANDLE,
              "Handle " + std::to_string(tag) + " is unknown or refers to a released slot.")
    {
    }
};

/* Records a null-argument failure and returns the status, for the generated null checks. */
OpenMcadStatus fail_null(const char* operation, const char* parameter) noexcept;

/* Records a diagnostic for the calling thread. Defined in openmcad_occt.cpp. */
void record_error(const char* operation, const char* detail) noexcept;

} /* namespace openmcad */

/* --- the exception firewall ----------------------------------------------------------------------
 *
 * Every generated entry point wraps its body in OPENMCAD_GUARD. OCCT throws Standard_Failure, the
 * standard library throws std::exception, and native code can throw things that are neither; all
 * three are caught here and converted to a status plus a thread-local diagnostic.
 *
 * This is not defensive style. A C++ exception crossing a C ABI is undefined behaviour, so an
 * uncaught throw here is not a bad error message -- it is a crash with no stack worth reading.
 */

#if defined(OPENMCAD_WITH_OCCT)
#  include <Standard_Failure.hxx>
#  define OPENMCAD_CATCH_KERNEL(op)                                         catch (const Standard_Failure& failure)                                 {                                                                           openmcad::record_error(op, failure.GetMessageString());                 return OPENMCAD_ERROR_KERNEL_FAILURE;                               }
#else
#  define OPENMCAD_CATCH_KERNEL(op)
#endif

#define OPENMCAD_GUARD(op, body)                                            try                                                                     {                                                                           body                                                                }                                                                       catch (const openmcad::kernel_error& error)                             {                                                                           openmcad::record_error(op, error.what());                               return error.status();                                              }                                                                       OPENMCAD_CATCH_KERNEL(op)                                               catch (const std::bad_alloc&)                                           {                                                                           openmcad::record_error(op, "out of memory");                            return OPENMCAD_ERROR_OUT_OF_MEMORY;                                }                                                                       catch (const std::exception& error)                                     {                                                                           openmcad::record_error(op, error.what());                               return OPENMCAD_ERROR_INTERNAL;                                     }                                                                       catch (...)                                                             {                                                                           openmcad::record_error(op, "unknown native exception");                 return OPENMCAD_ERROR_INTERNAL;                                     }

#endif /* OPENMCAD_TYPES_H */

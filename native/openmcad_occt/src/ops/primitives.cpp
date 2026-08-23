/*
 * Primitive construction (P1-T06, first slice).
 *
 * Every entity of a primitive is created from nothing, so the history map is entirely
 * add_created. That makes these the simplest operations to write and the right place to establish
 * the pattern the harder ones follow:
 *
 *   1. validate what the managed layer could not
 *   2. build
 *   3. store the result and tag its entities in canonical order
 *   4. record provenance for EVERY output entity, with a deliberate role
 *   5. hand back the shape and the history
 *
 * Step 4 is not optional. PLAN.md 5.1: an operation returning unrolled outputs is an incomplete
 * implementation, and the managed HistoryMapBuilder throws rather than accept one.
 */

#include <algorithm>
#include <cmath>

#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCone.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepPrimAPI_MakeTorus.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <gp_Ax2.hxx>
#include <gp_Quaternion.hxx>
#include <gp_Trsf.hxx>

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ops.g.h"
#include "openmcad_roles.h"

namespace openmcad::ops {

namespace {

/* Turns the eight doubles the managed side sends into an OCCT transform. */
gp_Trsf toTrsf(const Transform& placement)
{
    gp_Trsf trsf;

    gp_Quaternion rotation(placement.qx, placement.qy, placement.qz, placement.qw);
    trsf.SetRotation(rotation);
    trsf.SetTranslationPart(gp_Vec(placement.tx, placement.ty, placement.tz));

    if (placement.scale > 0.0 && std::abs(placement.scale - 1.0) > 1.0e-12)
    {
        gp_Trsf scaling;
        scaling.SetScale(gp_Pnt(0, 0, 0), placement.scale);
        trsf = trsf * scaling;
    }

    return trsf;
}

TopoDS_Shape place(const TopoDS_Shape& shape, const Transform& placement)
{
    const gp_Trsf trsf = toTrsf(placement);
    if (trsf.Form() == gp_Identity)
    {
        return shape;
    }

    BRepBuilderAPI_Transform mover(shape, trsf, /* copy */ true);
    if (!mover.IsDone())
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE, "The placement transform could not be applied.");
    }

    return mover.Shape();
}

/*
 * Stores a freshly created shape and records every one of its entities as created from nothing.
 *
 * Faces, edges and vertices each get their own role and an ordinal within that role, which is what
 * makes "the third edge of this box" nameable. Canonical ordering (see openmcad_canonical.h) is
 * what makes the ordinal mean the same thing on the next rebuild.
 */
void completePrimitive(const TopoDS_Shape& built, ShapeOut result, HistoryOut history)
{
    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    const std::pair<TopAbs_ShapeEnum, Role> kinds[] = {
        {TopAbs_FACE, Role::PrimitiveFace},
        {TopAbs_EDGE, Role::PrimitiveEdge},
        {TopAbs_VERTEX, Role::PrimitiveVertex},
    };

    for (const auto& [kind, role] : kinds)
    {
        for (uint64_t tag : tag_canonical(shape, kind))
        {
            record->add_created(tag, static_cast<int32_t>(role));
        }
    }

    result.set(shape);
    history.set(handles().store(std::move(record)));
}

} /* namespace */

void create_box(
    double size_x, double size_y, double size_z, const Transform& placement,
    ShapeOut result, HistoryOut history)
{
    // The managed layer validated these, but the shim is also reachable from a plugin or a test
    // harness, and a zero-sized box is a crash inside OCCT rather than an error out of it.
    if (size_x <= 0.0 || size_y <= 0.0 || size_z <= 0.0)
    {
        throw invalid_input("A box needs three positive dimensions.");
    }

    completePrimitive(
        place(BRepPrimAPI_MakeBox(size_x, size_y, size_z).Shape(), placement), result, history);
}

void create_cylinder(
    double radius, double height, const Transform& placement,
    ShapeOut result, HistoryOut history)
{
    if (radius <= 0.0 || height <= 0.0)
    {
        throw invalid_input("A cylinder needs a positive radius and height.");
    }

    completePrimitive(
        place(BRepPrimAPI_MakeCylinder(radius, height).Shape(), placement), result, history);
}

void create_sphere(double radius, const Transform& placement, ShapeOut result, HistoryOut history)
{
    if (radius <= 0.0)
    {
        throw invalid_input("A sphere needs a positive radius.");
    }

    completePrimitive(
        place(BRepPrimAPI_MakeSphere(radius).Shape(), placement), result, history);
}

void create_cone(
    double bottom_radius, double top_radius, double height, const Transform& placement,
    ShapeOut result, HistoryOut history)
{
    if (bottom_radius < 0.0 || top_radius < 0.0 || height <= 0.0)
    {
        throw invalid_input("A cone needs non-negative radii and a positive height.");
    }

    if (bottom_radius <= 0.0 && top_radius <= 0.0)
    {
        throw invalid_input("A cone needs at least one non-zero radius; both are zero.");
    }

    // OCCT rejects a cone whose radii are equal ("cone with two identic radii") rather than
    // degenerating to the obvious answer. The caller asked for the limiting case of a cone, which
    // is a cylinder, and that is a shape the kernel can build -- so build it, rather than making
    // every caller special-case the boundary of a continuous parameter.
    //
    // The comparison is relative: two radii of 0.05 that differ in the fifteenth decimal are the
    // same radius, and an absolute epsilon would either miss that at part scale or merge
    // genuinely different radii at micro scale.
    const double scale = std::max(bottom_radius, top_radius);
    if (std::abs(bottom_radius - top_radius) <= 1.0e-12 * std::max(scale, 1.0))
    {
        completePrimitive(
            place(BRepPrimAPI_MakeCylinder(scale, height).Shape(), placement), result, history);
        return;
    }

    completePrimitive(
        place(BRepPrimAPI_MakeCone(bottom_radius, top_radius, height).Shape(), placement),
        result, history);
}

void create_torus(
    double major_radius, double minor_radius, const Transform& placement,
    ShapeOut result, HistoryOut history)
{
    if (major_radius <= 0.0 || minor_radius <= 0.0)
    {
        throw invalid_input("A torus needs positive major and minor radii.");
    }

    if (minor_radius >= major_radius)
    {
        throw invalid_input(
            "The torus tube radius must be smaller than the major radius, or the tube passes "
            "through the axis and self-intersects.");
    }

    completePrimitive(
        place(BRepPrimAPI_MakeTorus(major_radius, minor_radius).Shape(), placement),
        result, history);
}

void create_polygon_profile(
    std::span<const Vec2> points, const Transform& frame, ShapeOut result, HistoryOut history)
{
    if (points.size() < 3)
    {
        throw invalid_input("A closed profile needs at least three points.");
    }

    BRepBuilderAPI_MakePolygon polygon;
    for (const Vec2& point : points)
    {
        polygon.Add(gp_Pnt(point.x, point.y, 0.0));
    }

    polygon.Close();

    if (!polygon.IsDone())
    {
        throw invalid_input(
            "The profile outline could not be closed. Check for coincident or collinear points.");
    }

    BRepBuilderAPI_MakeFace face(polygon.Wire(), /* onlyPlane */ true);
    if (!face.IsDone())
    {
        throw invalid_input(
            "The profile does not bound a planar region. It may be self-intersecting or enclose "
            "no area.");
    }

    completePrimitive(place(face.Shape(), frame), result, history);
}

} /* namespace openmcad::ops */

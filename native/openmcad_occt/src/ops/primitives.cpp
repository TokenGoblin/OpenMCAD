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
#include <vector>
#include <cmath>

#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCone.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepPrimAPI_MakeTorus.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepGProp.hxx>
#include <BRep_Tool.hxx>
#include <GProp_GProps.hxx>
#include <TopExp.hxx>
#include <TopoDS.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <gp_Ax1.hxx>
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
 * Names the parts of a primitive that is a surface of revolution.
 *
 * A box has six interchangeable planar faces and nothing to say about any of them, so PrimitiveFace
 * is the honest answer there. A cylinder is different: it has a lateral wall, two ends, and a seam
 * that exists only because the surface had to be cut open to be parameterised. Those are things a
 * user points at and a name has to survive -- "the cylindrical face", "the top rim" -- and calling
 * them all PrimitiveFace throws that away at the moment it is cheapest to record.
 *
 * The seam matters for a second reason. It is an artefact of parameterisation rather than of design
 * intent, so a user selecting "all edges" should be able to exclude it, and a kernel swap that
 * placed it elsewhere should be visible as a role change rather than as a silent renumbering.
 *
 * Start and end are decided by position along the axis, not by traversal order, so they mean the
 * same thing after the primitive is moved.
 */
void nameRevolution(
    HistoryRecord& record, ShapeRef shape, const TopoDS_Shape& built, const gp_Ax1& axis)
{
    // Faces: planar ones are caps, anything curved is the wall.
    std::vector<std::pair<double, uint64_t>> caps;

    for (const TopoDS_Shape& entity : enumerate_canonical(built, TopAbs_FACE))
    {
        const TopoDS_Face face = TopoDS::Face(entity);
        const uint64_t tag = handles().store_entity(shape, face).tag;

        GProp_GProps props;
        BRepGProp::SurfaceProperties(face, props);
        const double along = gp_Vec(axis.Location(), props.CentreOfMass()).Dot(gp_Vec(axis.Direction()));

        if (BRepAdaptor_Surface(face).GetType() == GeomAbs_Plane)
        {
            caps.emplace_back(along, tag);
        }
        else
        {
            record.add_created(tag, static_cast<int32_t>(Role::SideWall));
        }
    }

    std::sort(caps.begin(), caps.end());
    for (size_t i = 0; i < caps.size(); ++i)
    {
        // A cone with a zero top radius has one cap, and it is the one the profile started from.
        const Role role = i == 0 ? Role::StartCap : Role::EndCap;
        record.add_created(caps[i].second, static_cast<int32_t>(role));
    }

    // Edges: seams and degenerate poles are parameterisation, circles bounding a cap are the
    // profile at each end, anything else runs along the wall.
    ShapeAncestorMap edgeToFaces;
    TopExp::MapShapesAndAncestors(built, TopAbs_EDGE, TopAbs_FACE, edgeToFaces);

    std::vector<std::pair<double, uint64_t>> rims;

    for (const TopoDS_Shape& entity : enumerate_canonical(built, TopAbs_EDGE))
    {
        const TopoDS_Edge edge = TopoDS::Edge(entity);
        const uint64_t tag = handles().store_entity(shape, edge).tag;

        bool seam = BRep_Tool::Degenerated(edge);
        bool bounded = false;

        if (!seam && edgeToFaces.Contains(edge))
        {
            for (const TopoDS_Shape& face : edgeToFaces.FindFromKey(edge))
            {
                const TopoDS_Face owner = TopoDS::Face(face);
                seam = seam || BRep_Tool::IsClosed(edge, owner);
                bounded = bounded || BRepAdaptor_Surface(owner).GetType() == GeomAbs_Plane;
            }
        }

        if (seam)
        {
            record.add_created(tag, static_cast<int32_t>(Role::Seam));
        }
        else if (bounded)
        {
            GProp_GProps props;
            BRepGProp::LinearProperties(edge, props);
            rims.emplace_back(
                gp_Vec(axis.Location(), props.CentreOfMass()).Dot(gp_Vec(axis.Direction())), tag);
        }
        else
        {
            record.add_created(tag, static_cast<int32_t>(Role::SideEdge));
        }
    }

    std::sort(rims.begin(), rims.end());
    for (size_t i = 0; i < rims.size(); ++i)
    {
        const Role role = i == 0 ? Role::StartProfileEdge : Role::EndProfileEdge;
        record.add_created(rims[i].second, static_cast<int32_t>(role));
    }

    for (uint64_t tag : tag_canonical(shape, TopAbs_VERTEX))
    {
        record.add_created(tag, static_cast<int32_t>(Role::PrimitiveVertex));
    }
}

/*
 * Stores a freshly created shape and records every one of its entities as created from nothing.
 *
 * Faces, edges and vertices each get their own role and an ordinal within that role, which is what
 * makes "the third edge of this box" nameable. Canonical ordering (see openmcad_canonical.h) is
 * what makes the ordinal mean the same thing on the next rebuild.
 */
void completePrimitive(
    const TopoDS_Shape& built, ShapeOut result, HistoryOut history,
    const gp_Ax1* revolutionAxis = nullptr)
{
    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    if (revolutionAxis != nullptr)
    {
        nameRevolution(*record, shape, built, *revolutionAxis);
    }
    else
    {
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
    }

    result.set(shape);
    history.set(handles().store(std::move(record)));
}

/* Where a primitive's axis of revolution ends up once the placement has been applied. */
gp_Ax1 placedAxis(const Transform& placement)
{
    return gp_Ax1(gp_Pnt(0.0, 0.0, 0.0), gp_Dir(0.0, 0.0, 1.0)).Transformed(toTrsf(placement));
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

    const gp_Ax1 axis = placedAxis(placement);
    completePrimitive(
        place(BRepPrimAPI_MakeCylinder(radius, height).Shape(), placement), result, history,
        &axis);
}

void create_sphere(double radius, const Transform& placement, ShapeOut result, HistoryOut history)
{
    if (radius <= 0.0)
    {
        throw invalid_input("A sphere needs a positive radius.");
    }

    const gp_Ax1 axis = placedAxis(placement);
    completePrimitive(
        place(BRepPrimAPI_MakeSphere(radius).Shape(), placement), result, history, &axis);
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
        const gp_Ax1 axis = placedAxis(placement);
        completePrimitive(
            place(BRepPrimAPI_MakeCylinder(scale, height).Shape(), placement), result, history,
            &axis);
        return;
    }

    const gp_Ax1 axis = placedAxis(placement);
    completePrimitive(
        place(BRepPrimAPI_MakeCone(bottom_radius, top_radius, height).Shape(), placement),
        result, history, &axis);
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

    const gp_Ax1 axis = placedAxis(placement);
    completePrimitive(
        place(BRepPrimAPI_MakeTorus(major_radius, minor_radius).Shape(), placement),
        result, history, &axis);
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

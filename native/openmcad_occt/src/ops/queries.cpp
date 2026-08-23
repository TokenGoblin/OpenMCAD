/*
 * Queries and history access (P1-T06, first slice).
 */

#include <BRepBndLib.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepGProp.hxx>
#include <Bnd_Box.hxx>
#include <GProp_GProps.hxx>
#include <TopExp.hxx>
#include <TopoDS_Shape.hxx>

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ops.g.h"

namespace openmcad::ops {

namespace {

TopAbs_ShapeEnum toTopAbs(int32_t kind)
{
    // Mirrors OpenMCAD.Kernel.SubEntityKind. Append-only on both sides.
    switch (kind)
    {
        case 1: return TopAbs_VERTEX;
        case 2: return TopAbs_EDGE;
        case 3: return TopAbs_WIRE;
        case 4: return TopAbs_FACE;
        case 5: return TopAbs_SHELL;
        case 6: return TopAbs_SOLID;
        case 7: return TopAbs_COMPOUND;
        default:
            throw invalid_input("Unknown entity kind " + std::to_string(kind) + ".");
    }
}

int32_t fromTopAbs(TopAbs_ShapeEnum kind)
{
    switch (kind)
    {
        case TopAbs_VERTEX:   return 1;
        case TopAbs_EDGE:     return 2;
        case TopAbs_WIRE:     return 3;
        case TopAbs_FACE:     return 4;
        case TopAbs_SHELL:    return 5;
        case TopAbs_SOLID:    return 6;
        case TopAbs_COMPOUND: return 7;
        default:              return 0;
    }
}

int countOf(const TopoDS_Shape& shape, TopAbs_ShapeEnum kind)
{
    ShapeIndexedMap map;
    TopExp::MapShapes(shape, kind, map);
    return map.Extent();
}

} /* namespace */

void mass_properties(
    ShapeRef shape, double density, OutBuffer<double> values, int32_t& accuracy)
{
    const TopoDS_Shape& solid = handles().resolve(shape);

    GProp_GProps volume;
    GProp_GProps surface;
    BRepGProp::VolumeProperties(solid, volume);
    BRepGProp::SurfaceProperties(solid, surface);

    const gp_Pnt centre = volume.Mass() > 0.0 ? volume.CentreOfMass() : surface.CentreOfMass();
    const gp_Mat inertia = volume.MatrixOfInertia();

    // Eleven doubles, in the order the managed side unpacks them: volume, area, centroid xyz,
    // then Ixx Iyy Izz Ixy Ixz Iyz. OCCT reports inertia for unit density, so scale it here.
    const double scale = density;
    const double figures[11] = {
        volume.Mass(),
        surface.Mass(),
        centre.X(), centre.Y(), centre.Z(),
        inertia.Value(1, 1) * scale,
        inertia.Value(2, 2) * scale,
        inertia.Value(3, 3) * scale,
        -inertia.Value(1, 2) * scale,
        -inertia.Value(1, 3) * scale,
        -inertia.Value(2, 3) * scale,
    };

    // OCCT integrates exactly over the analytic geometry rather than over a tessellation.
    accuracy = 0;
    values.write(std::span<const double>(figures, 11));
}

void bounding_box(ShapeRef shape, OutBuffer<double> values)
{
    const TopoDS_Shape& solid = handles().resolve(shape);

    Bnd_Box box;

    // AddOptimal, not Add, and both flags off. Three deliberate choices:
    //
    //   Add() bounds a surface by the convex hull of its control points, which for anything curved
    //   is loose -- a cylinder comes out bounded by its inscribing square prism corners. AddOptimal
    //   finds the true extrema. It costs more; a bounding box that is wrong is worth less.
    //
    //   useTriangulation = false. If it were true the answer would come from the tessellation when
    //   one happens to be cached and from the geometry otherwise, so bounds would silently change
    //   depending on whether anything had rendered the body yet. ADR-0011 does not allow a query to
    //   depend on unrelated history.
    //
    //   useShapeTolerance = false, then SetGap(0). Otherwise every face's tolerance pads the result
    //   outward -- 1e-7 m on a box that was asked for exactly. Callers sizing stock or checking fit
    //   want the geometry's extent, not the extent plus the modeller's uncertainty.
    BRepBndLib::AddOptimal(solid, box, /* useTriangulation */ false, /* useShapeTolerance */ false);
    box.SetGap(0.0);

    if (box.IsVoid())
    {
        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, "The shape has no extent to bound.");
    }

    double xmin, ymin, zmin, xmax, ymax, zmax;
    box.Get(xmin, ymin, zmin, xmax, ymax, zmax);

    const double figures[6] = {xmin, ymin, zmin, xmax, ymax, zmax};
    values.write(std::span<const double>(figures, 6));
}

void topology_counts(ShapeRef shape, OutBuffer<int32_t> values)
{
    const TopoDS_Shape& solid = handles().resolve(shape);

    const int32_t counts[6] = {
        countOf(solid, TopAbs_SOLID),
        countOf(solid, TopAbs_SHELL),
        countOf(solid, TopAbs_FACE),
        countOf(solid, TopAbs_WIRE),
        countOf(solid, TopAbs_EDGE),
        countOf(solid, TopAbs_VERTEX),
    };

    values.write(std::span<const int32_t>(counts, 6));
}

void enumerate(ShapeRef shape, int32_t kind, OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t> tags = tag_canonical(shape, toTopAbs(kind));
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

void entity_kind(ShapeRef shape, EntityRef entity, int32_t& kind)
{
    // Confirms the entity belongs to this shape rather than merely existing somewhere.
    if (handles().owner_of(entity).tag != shape.tag)
    {
        throw kernel_error(
            OPENMCAD_ERROR_INVALID_HANDLE,
            "The entity belongs to a different body than the one it was queried against.");
    }

    kind = fromTopAbs(handles().resolve(entity).ShapeType());
}

void check_validity(ShapeRef shape, int32_t& is_valid, int32_t& is_closed)
{
    const TopoDS_Shape& solid = handles().resolve(shape);

    BRepCheck_Analyzer analyzer(solid);
    is_valid = analyzer.IsValid() ? 1 : 0;

    // "Closed" means every shell bounds a volume, which is what makes a solid a solid. A profile
    // is legitimately open, so this is reported rather than asserted.
    is_closed = solid.Closed() ? 1 : 0;

    if (is_closed == 0 && countOf(solid, TopAbs_SOLID) > 0)
    {
        // OCCT does not always set the flag even when the shell is closed, so fall back to asking.
        ShapeIndexedMap shells;
        TopExp::MapShapes(solid, TopAbs_SHELL, shells);

        bool allClosed = shells.Extent() > 0;
        for (int i = 1; i <= shells.Extent() && allClosed; ++i)
        {
            allClosed = shells(i).Closed();
        }

        is_closed = allClosed ? 1 : 0;
    }
}

/* --- history access ------------------------------------------------------------------------- */

void history_generated(HistoryRef history, EntityRef input, OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t> tags = handles().resolve(history).generated_of(input.tag);
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

void history_modified(HistoryRef history, EntityRef input, OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t> tags = handles().resolve(history).modified_of(input.tag);
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

void history_is_deleted(HistoryRef history, EntityRef input, int32_t& deleted)
{
    deleted = handles().resolve(history).deleted.count(input.tag) != 0 ? 1 : 0;
}

void history_new_entities(HistoryRef history, OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t> tags = handles().resolve(history).new_entities();
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

void history_role_of(HistoryRef history, EntityRef output, int32_t& role)
{
    const HistoryRecord& record = handles().resolve(history);
    auto it = record.roles.find(output.tag);

    // Zero is Unknown, which the managed side treats as "this map does not describe that entity"
    // rather than as an error: asking about an unrelated entity is a legitimate question.
    role = it == record.roles.end() ? 0 : it->second;
}

void history_source_of(HistoryRef history, EntityRef output, uint64_t& source)
{
    const HistoryRecord& record = handles().resolve(history);
    auto it = record.sources.find(output.tag);
    source = it == record.sources.end() ? 0 : it->second;
}

void history_outputs(HistoryRef history, OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t> tags = handles().resolve(history).outputs();
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

void history_inputs(HistoryRef history, OutBuffer<uint64_t> entities)
{
    const std::vector<uint64_t> tags = handles().resolve(history).inputs();
    entities.write(std::span<const uint64_t>(tags.data(), tags.size()));
}

} /* namespace openmcad::ops */

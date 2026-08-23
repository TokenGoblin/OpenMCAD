#include "openmcad_canonical.h"
#include "openmcad_roles.h"

#include <algorithm>
#include <cmath>
#include <tuple>

#include <BRepGProp.hxx>
#include <BRep_Tool.hxx>
#include <GProp_GProps.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Vertex.hxx>
#include <gp_Pnt.hxx>

namespace openmcad {

namespace {

/*
 * Rounds a coordinate to a grid coarse enough to absorb floating-point noise and fine enough that
 * no real feature collapses onto its neighbour. A nanometre: a thousand times below the modelling
 * tolerance, a million times above the noise in a double at part scale.
 */
double quantise(double value)
{
    constexpr double grid = 1.0e-9;
    return std::round(value / grid) * grid;
}

struct SortKey
{
    double measure = 0.0;
    double x = 0.0, y = 0.0, z = 0.0;
    int traversal = 0;

    bool operator<(const SortKey& other) const
    {
        return std::tie(measure, x, y, z, traversal)
             < std::tie(other.measure, other.x, other.y, other.z, other.traversal);
    }
};

SortKey keyOf(const TopoDS_Shape& entity, TopAbs_ShapeEnum kind, int traversal)
{
    SortKey key;
    key.traversal = traversal;

    GProp_GProps props;
    gp_Pnt point;

    switch (kind)
    {
        case TopAbs_FACE:
            BRepGProp::SurfaceProperties(entity, props);
            key.measure = quantise(props.Mass());
            point = props.CentreOfMass();
            break;

        case TopAbs_EDGE:
            BRepGProp::LinearProperties(entity, props);
            key.measure = quantise(props.Mass());
            point = props.CentreOfMass();
            break;

        case TopAbs_VERTEX:
            key.measure = 0.0;
            point = BRep_Tool::Pnt(TopoDS::Vertex(entity));
            break;

        default:
            BRepGProp::VolumeProperties(entity, props);
            key.measure = quantise(props.Mass());
            point = props.CentreOfMass();
            break;
    }

    key.x = quantise(point.X());
    key.y = quantise(point.Y());
    key.z = quantise(point.Z());
    return key;
}

} /* namespace */

std::vector<TopoDS_Shape> enumerate_canonical(const TopoDS_Shape& shape, TopAbs_ShapeEnum kind)
{
    // Indexed map rather than raw exploration: it deduplicates entities shared between faces, so
    // an edge between two faces appears once rather than twice.
    ShapeIndexedMap map;
    TopExp::MapShapes(shape, kind, map);

    std::vector<std::pair<SortKey, TopoDS_Shape>> keyed;
    keyed.reserve(static_cast<size_t>(map.Extent()));

    for (int i = 1; i <= map.Extent(); ++i)
    {
        keyed.emplace_back(keyOf(map(i), kind, i), map(i));
    }

    std::stable_sort(
        keyed.begin(), keyed.end(),
        [](const auto& a, const auto& b) { return a.first < b.first; });

    std::vector<TopoDS_Shape> result;
    result.reserve(keyed.size());
    for (auto& entry : keyed)
    {
        result.push_back(entry.second);
    }

    return result;
}

std::vector<uint64_t> tag_canonical(ShapeRef owner, TopAbs_ShapeEnum kind)
{
    const TopoDS_Shape& shape = handles().resolve(owner);
    std::vector<TopoDS_Shape> entities = enumerate_canonical(shape, kind);

    std::vector<uint64_t> tags;
    tags.reserve(entities.size());
    for (const TopoDS_Shape& entity : entities)
    {
        tags.push_back(handles().store_entity(owner, entity).tag);
    }

    return tags;
}

void sweep_retained(
    HistoryRecord& record,
    const TopoDS_Shape& before,
    ShapeRef beforeRef,
    const TopoDS_Shape& after,
    ShapeRef afterRef,
    TopAbs_ShapeEnum kind)
{
    ShapeIndexedMap survivors;
    TopExp::MapShapes(after, kind, survivors);

    for (const TopoDS_Shape& input : enumerate_canonical(before, kind))
    {
        const uint64_t inputTag = handles().store_entity(beforeRef, input).tag;

        // Only a modified entry or a deletion settles whether an input survived. Generation does
        // not, and treating it as if it did was a bug: a profile edge generates the side wall it
        // sweeps AND survives as the bottom edge of the prism. Skipping it on the strength of the
        // generation alone left the bottom edge unaccounted for, to be picked up later as a seam --
        // the wrong name for the very edge the user drew.
        //
        // This is the same distinction HistoryMapBuilder draws by permitting Deleted alongside
        // Generated but not alongside Modified.
        if (record.modified.count(inputTag) != 0 || record.deleted.count(inputTag) != 0)
        {
            continue;
        }

        // IsSame compares TShape identity and orientation-insensitively, which is what "the same
        // entity came through untouched" means. Contains uses that comparison.
        if (survivors.Contains(input))
        {
            const uint64_t outputTag = handles().store_entity(afterRef, input).tag;
            record.add_modified(inputTag, outputTag, static_cast<int32_t>(Role::Retained));
        }
        else
        {
            record.add_deleted(inputTag);
        }
    }
}


void sweep_created(
    HistoryRecord& record, ShapeRef afterRef, TopAbs_ShapeEnum kind, int32_t role)
{
    // Claimed means "already an output of something", which is exactly what roles is keyed by:
    // every add_generated, add_modified and add_created writes one.
    for (uint64_t tag : tag_canonical(afterRef, kind))
    {
        if (record.roles.count(tag) == 0)
        {
            record.add_created(tag, role);
        }
    }
}

} /* namespace openmcad */

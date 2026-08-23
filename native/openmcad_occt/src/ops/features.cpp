/*
 * Feature operations: sweeps, booleans and blends (P1-T06).
 *
 * These are the operations whose history actually matters. A primitive creates everything from
 * nothing, so its map is trivial; these consume an existing body, and the whole topological naming
 * scheme (ADR-0005) rests on their maps being complete and correctly roled.
 *
 * Every body here follows the same five steps, in the same order, for a reason:
 *
 *   1. validate                    -- OCCT crashes rather than complains on some bad input
 *   2. build, with parallelism off -- ADR-0011; see SetRunParallel below
 *   3. ask the builder what it did -- Generated / Modified / IsDeleted
 *   4. sweep for retained inputs   -- the spike's finding: OCCT does not report these at all
 *   5. sweep for created outputs   -- so no output entity is left unexplained
 *
 * Steps 4 and 5 are not belt-and-braces. Step 3 alone is silently, majority-incomplete: cutting a
 * cylinder from a box, OCCT described two of the box's six faces and said nothing about the rest.
 */

#include <cmath>
#include <memory>
#include <sstream>

#include <BRepAlgoAPI_BooleanOperation.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeShape.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepFilletAPI_MakeChamfer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepPrimAPI_MakeSweep.hxx>
#include <BRepPrimAPI_MakeRevol.hxx>
#include <BRepTools.hxx>
#include <TopExp.hxx>
#include <TopoDS.hxx>
#include <gp.hxx>
#include <gp_Ax1.hxx>
#include <gp_Vec.hxx>

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ops.g.h"
#include "openmcad_roles.h"

namespace openmcad::ops {

namespace {

/*
 * Mirrors OpenMCAD.Kernel.RetryRung. The shim reports the managed enum's values directly rather
 * than a private numbering the managed side would have to translate: two numberings for one
 * concept is how a translation table ends up wrong in one direction only.
 */
enum class Rung : int32_t
{
    NotApplicable = 0,
    ModelTolerance = 1,
    Conditioned = 2,
    FuzzyTolerance = 3,
};

/* The three entity kinds an operation must account for, coarsest first. */
constexpr TopAbs_ShapeEnum kMappedKinds[] = {TopAbs_FACE, TopAbs_EDGE, TopAbs_VERTEX};

/*
 * Every entity of the result that a history is allowed to name.
 *
 * OCCT reports intermediate entities: a prism's Generated() names faces that a later canonisation
 * step merged away, and they are not in the shape it hands back. Recording one produces a history
 * naming an entity the result does not have, which the managed side rejects outright -- correctly,
 * because a name that resolves to nothing is worse than no name at all.
 *
 * All three kinds go into one map because generation crosses kinds: a swept edge produces a face,
 * and a swept vertex produces an edge. Filtering each kind against only its own kind silently drops
 * exactly the relationships that make a side wall nameable after the profile edge that swept it.
 *
 * An input whose only reported successors were dropped falls through to sweep_retained, which looks
 * it up in the output and settles it as retained or deleted.
 */
ShapeIndexedMap nameableEntities(const TopoDS_Shape& shape)
{
    ShapeIndexedMap present;
    for (TopAbs_ShapeEnum kind : {TopAbs_FACE, TopAbs_EDGE, TopAbs_VERTEX})
    {
        TopExp::MapShapes(shape, kind, present);
    }

    return present;
}

/* The role given to an output of this kind that nothing else claimed. */
struct FallbackRoles
{
    Role face;
    Role edge;
    Role vertex;

    Role of(TopAbs_ShapeEnum kind) const
    {
        switch (kind)
        {
            case TopAbs_FACE: return face;
            case TopAbs_EDGE: return edge;
            default:          return vertex;
        }
    }
};

/* Copies one builder's account of one input shape into the record. */
void mapReported(
    HistoryRecord& record,
    BRepBuilderAPI_MakeShape& builder,
    const TopoDS_Shape& before,
    ShapeRef beforeRef,
    const ShapeIndexedMap& present,
    ShapeRef afterRef,
    TopAbs_ShapeEnum kind,
    Role generatedRole,
    Role modifiedRole)
{
    for (const TopoDS_Shape& input : enumerate_canonical(before, kind))
    {
        const uint64_t inputTag = handles().store_entity(beforeRef, input).tag;

        for (const TopoDS_Shape& made : builder.Generated(input))
        {
            if (!present.Contains(made))
            {
                continue;
            }

            record.add_generated(
                inputTag,
                handles().store_entity(afterRef, made).tag,
                static_cast<int32_t>(generatedRole));
        }

        for (const TopoDS_Shape& changed : builder.Modified(input))
        {
            if (!present.Contains(changed))
            {
                continue;
            }

            record.add_modified(
                inputTag,
                handles().store_entity(afterRef, changed).tag,
                static_cast<int32_t>(modifiedRole));
        }

        // Deliberately not "else if". An edge that is filleted away is both deleted and the reason
        // the blend face exists; HistoryMapBuilder permits that pair and the fillet case needs it.
        if (builder.IsDeleted(input))
        {
            record.add_deleted(inputTag);
        }
    }
}

/* Runs steps 3 to 5 for every entity kind, over every input shape the operation consumed. */
void mapAll(
    HistoryRecord& record,
    BRepBuilderAPI_MakeShape& builder,
    const std::vector<std::pair<TopoDS_Shape, ShapeRef>>& inputs,
    const TopoDS_Shape& after,
    ShapeRef afterRef,
    Role generatedRole,
    Role modifiedRole,
    FallbackRoles fallback)
{
    const ShapeIndexedMap present = nameableEntities(after);

    for (TopAbs_ShapeEnum kind : kMappedKinds)
    {
        for (const auto& entry : inputs)
        {
            mapReported(
                record, builder, entry.first, entry.second, present, afterRef, kind,
                generatedRole, modifiedRole);

            sweep_retained(record, entry.first, entry.second, after, afterRef, kind);
        }
    }

    // Every kind is mapped before anything is swept as created, and the two loops are separate for
    // that reason alone. Generation crosses kinds -- a filleted edge produces a face -- so a
    // created-sweep run at the end of the face pass would claim the blend face as an unexplained
    // corner face before the edge pass ever got to name it properly. Role assignment is
    // first-wins, so that first, wrong answer would be the one that stuck.
    for (TopAbs_ShapeEnum kind : kMappedKinds)
    {
        sweep_created(record, afterRef, kind, static_cast<int32_t>(fallback.of(kind)));
    }
}

/*
 * Pulls OCCT's own diagnosis out of a failed algorithm rather than inventing a message.
 *
 * Templated on the concrete algorithm because BRepAlgoAPI_Algo inherits BOPAlgo_Options
 * *protected* and re-exports DumpErrors by name -- so the members are reachable but a reference to
 * the base is not.
 */
template <typename Algorithm>
[[noreturn]] void reportFailure(Algorithm& algorithm, const char* what)
{
    std::ostringstream detail;
    algorithm.DumpErrors(detail);

    std::string message = std::string(what) + " failed.";
    const std::string reported = detail.str();
    if (!reported.empty())
    {
        message += " The kernel reported: " + reported;
    }

    throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, message);
}

/*
 * The profile a sweep should actually consume.
 *
 * A capped sweep needs a face to sweep into a solid; an uncapped one needs a wire, or OCCT hands
 * back a solid regardless of what was asked for. Callers pass whichever they happen to have, so
 * this converts rather than rejecting.
 */
TopoDS_Shape sweepProfile(const TopoDS_Shape& profile, bool capped)
{
    const TopAbs_ShapeEnum kind = profile.ShapeType();

    if (capped)
    {
        if (kind == TopAbs_FACE)
        {
            return profile;
        }

        if (kind == TopAbs_WIRE)
        {
            BRepBuilderAPI_MakeFace face(TopoDS::Wire(profile), /* onlyPlane */ true);
            if (!face.IsDone())
            {
                throw invalid_input(
                    "A capped sweep needs a profile that bounds a planar region, and this wire "
                    "does not. It may be open or self-intersecting.");
            }

            return face.Shape();
        }

        throw invalid_input("A capped sweep needs a face or a closed wire as its profile.");
    }

    if (kind == TopAbs_FACE)
    {
        return BRepTools::OuterWire(TopoDS::Face(profile));
    }

    if (kind == TopAbs_WIRE || kind == TopAbs_EDGE)
    {
        return profile;
    }

    throw invalid_input("An uncapped sweep needs a wire, an edge, or a face to take the wire from.");
}

/* Records the two end caps of a sweep, which OCCT reports separately from Generated. */
void mapCaps(
    HistoryRecord& record,
    BRepPrimAPI_MakeSweep& builder,
    const TopoDS_Shape& profile,
    ShapeRef beforeRef,
    const TopoDS_Shape& after,
    ShapeRef afterRef)
{
    const uint64_t profileTag = handles().store_entity(beforeRef, profile).tag;

    const std::pair<TopoDS_Shape, Role> caps[] = {
        {builder.FirstShape(), Role::StartCap},
        {builder.LastShape(), Role::EndCap},
    };

    ShapeIndexedMap present;
    TopExp::MapShapes(after, TopAbs_FACE, present);


    for (const auto& entry : caps)
    {
        // A full revolution has no caps, and OCCT signals that with a null shape rather than an
        // error, so this is the normal path rather than a defensive one. The containment check is
        // the same rule as in mapReported: a cap that was merged away is not in the result.
        if (!entry.first.IsNull() && present.Contains(entry.first))
        {
            record.add_generated(
                profileTag,
                handles().store_entity(afterRef, entry.first).tag,
                static_cast<int32_t>(entry.second));
        }
    }
}

/* Shared tail of extrude and revolve. */
void completeSweep(
    BRepPrimAPI_MakeSweep& builder,
    const TopoDS_Shape& profile,
    ShapeRef profileRef,
    bool capped,
    ShapeOut result,
    HistoryOut history)
{
    const TopoDS_Shape built = builder.Shape();
    if (built.IsNull())
    {
        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, "The sweep produced no geometry.");
    }

    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    if (capped)
    {
        mapCaps(*record, builder, profile, profileRef, built, shape);
    }

    // A swept edge becomes a side wall; a swept vertex becomes a side edge. One role cannot cover
    // the pair, which is why this loops per kind rather than calling mapAll.
    const ShapeIndexedMap present = nameableEntities(built);

    for (TopAbs_ShapeEnum kind : kMappedKinds)
    {
        const Role generated = kind == TopAbs_EDGE   ? Role::SideWall
                             : kind == TopAbs_VERTEX ? Role::SideEdge
                                                     : Role::Transformed;

        mapReported(
            *record, builder, profile, profileRef, present, shape, kind, generated,
            Role::Transformed);

        sweep_retained(*record, profile, profileRef, built, shape, kind);
    }

    // A closed sweep grows a seam edge that belongs to no input edge, and a revolve whose profile
    // touches the axis grows an apex vertex the same way. Naming them is the point of the roles.
    sweep_created(*record, shape, TopAbs_FACE, static_cast<int32_t>(Role::SideWall));
    sweep_created(*record, shape, TopAbs_EDGE, static_cast<int32_t>(Role::Seam));
    sweep_created(*record, shape, TopAbs_VERTEX, static_cast<int32_t>(Role::Apex));


    result.set(shape);
    history.set(handles().store(std::move(record)));
}

} /* namespace */

void extrude(
    ShapeRef profile, const Vec3& direction, double distance, bool capped,
    ShapeOut result, HistoryOut history)
{
    if (distance == 0.0)
    {
        throw invalid_input("An extrusion needs a non-zero distance.");
    }

    gp_Vec along(direction.x, direction.y, direction.z);
    if (along.Magnitude() < gp::Resolution())
    {
        throw invalid_input("The extrusion direction has no length.");
    }

    // The IDL says the direction is unit length. Normalising rather than trusting it means a
    // caller that passed a scaled vector gets the distance it asked for instead of a silent
    // multiple of it.
    along.Normalize();
    along *= distance;

    const TopoDS_Shape swept = sweepProfile(handles().resolve(profile), capped);

    BRepPrimAPI_MakePrism builder(swept, along, /* copy */ false, /* canonize */ true);
    if (!builder.IsDone())
    {
        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, "The extrusion could not be built.");
    }

    completeSweep(builder, swept, profile, capped, result, history);
}

void revolve(
    ShapeRef profile, const Vec3& axis_point, const Vec3& axis_direction, double angle,
    bool capped, ShapeOut result, HistoryOut history)
{
    constexpr double kTwoPi = 6.283185307179586476925286766559;

    if (angle == 0.0)
    {
        throw invalid_input("A revolution needs a non-zero angle.");
    }

    if (std::abs(angle) > kTwoPi + 1.0e-9)
    {
        throw invalid_input("A revolution cannot exceed a full turn.");
    }

    gp_Vec direction(axis_direction.x, axis_direction.y, axis_direction.z);
    if (direction.Magnitude() < gp::Resolution())
    {
        throw invalid_input("The revolution axis has no direction.");
    }

    const gp_Ax1 axis(gp_Pnt(axis_point.x, axis_point.y, axis_point.z), gp_Dir(direction));

    // A full turn closes on itself and has no caps to make, whatever the caller asked for.
    const bool full = std::abs(std::abs(angle) - kTwoPi) < 1.0e-9;
    const bool wantCaps = capped && !full;

    const TopoDS_Shape swept = sweepProfile(handles().resolve(profile), capped);

    BRepPrimAPI_MakeRevol builder(swept, axis, angle, /* copy */ false);
    if (!builder.IsDone())
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE,
            "The revolution could not be built. The profile may cross the axis.");
    }

    completeSweep(builder, swept, profile, wantCaps, result, history);
}

void boolean(
    int32_t operation, ShapeRef target, std::span<const uint64_t> tools,
    double tolerance, double fuzzy_tolerance,
    ShapeOut result, HistoryOut history, int32_t& rung)
{
    if (tools.empty())
    {
        throw invalid_input("A boolean needs at least one tool body.");
    }

    const TopoDS_Shape& targetShape = handles().resolve(target);

    ShapeList arguments;
    ShapeList toolShapes;
    arguments.Append(targetShape);

    std::vector<std::pair<TopoDS_Shape, ShapeRef>> inputs;
    inputs.emplace_back(targetShape, target);

    for (uint64_t tag : tools)
    {
        const ShapeRef toolRef{tag};
        const TopoDS_Shape& tool = handles().resolve(toolRef);
        toolShapes.Append(tool);
        inputs.emplace_back(tool, toolRef);
    }

    std::unique_ptr<BRepAlgoAPI_BooleanOperation> builder;
    switch (operation)
    {
        case 0: builder = std::make_unique<BRepAlgoAPI_Fuse>(); break;
        case 1: builder = std::make_unique<BRepAlgoAPI_Cut>(); break;
        case 2: builder = std::make_unique<BRepAlgoAPI_Common>(); break;
        default:
            throw invalid_input(
                "Unknown boolean operation " + std::to_string(operation)
                + ". Expected 0 union, 1 subtract, or 2 intersect.");
    }

    builder->SetArguments(arguments);
    builder->SetTools(toolShapes);

    // ADR-0011. OCCT's parallel boolean partitions work across threads, and the order faces are
    // merged in can change which of several tolerance-equal results comes out. Determinism is
    // worth more here than the throughput, and ADR-0004 already confines the kernel to one thread.
    builder->SetRunParallel(false);

    // The inputs are still owned by the handle table and may be referenced by another feature in
    // the tree. Letting OCCT modify them in place would corrupt the history of operations that
    // have already run.
    builder->SetNonDestructive(true);

    if (tolerance > 0.0)
    {
        builder->SetFuzzyValue(tolerance);
    }

    // The ladder proper is P1-T11. Until it exists this reports what actually happened rather
    // than a placeholder: a plain attempt at model tolerance, or -- if the caller asked for one
    // explicitly -- a fuzzy attempt.
    rung = static_cast<int32_t>(Rung::ModelTolerance);
    if (fuzzy_tolerance > 0.0)
    {
        builder->SetFuzzyValue(fuzzy_tolerance);
        rung = static_cast<int32_t>(Rung::FuzzyTolerance);
    }

    builder->Build();

    if (builder->HasErrors())
    {
        reportFailure(*builder, "The boolean operation");
    }

    const TopoDS_Shape built = builder->Shape();
    if (built.IsNull())
    {
        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, "The boolean produced no geometry.");
    }

    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    mapAll(
        *record, *builder, inputs, built, shape,
        Role::SplitPositive, Role::Trimmed,
        FallbackRoles{Role::CoincidentFace, Role::IntersectionEdge, Role::IntersectionVertex});

    result.set(shape);
    history.set(handles().store(std::move(record)));
}

namespace {

/*
 * Shared front half of fillet and chamfer: both take the same edge/value pairing, and both have to
 * reject the same mistakes.
 */
std::vector<std::pair<TopoDS_Edge, double>> blendInputs(
    ShapeRef body, std::span<const uint64_t> edges, std::span<const double> values,
    const char* valueName)
{
    if (edges.empty())
    {
        throw invalid_input("No edges were given to blend.");
    }

    if (edges.size() != values.size())
    {
        throw invalid_input(
            "There are " + std::to_string(edges.size()) + " edges but "
            + std::to_string(values.size()) + " " + valueName
            + ". The operation needs exactly one per edge.");
    }

    std::vector<std::pair<TopoDS_Edge, double>> selected;
    selected.reserve(edges.size());

    for (size_t i = 0; i < edges.size(); ++i)
    {
        if (values[i] <= 0.0)
        {
            throw invalid_input(
                "Edge " + std::to_string(i) + " was given a non-positive "
                + std::string(valueName) + ".");
        }

        const EntityRef edge{edges[i]};
        if (handles().owner_of(edge).tag != body.tag)
        {
            throw kernel_error(
                OPENMCAD_ERROR_INVALID_HANDLE,
                "Edge " + std::to_string(i) + " belongs to a different body.");
        }

        const TopoDS_Shape& entity = handles().resolve(edge);
        if (entity.ShapeType() != TopAbs_EDGE)
        {
            throw invalid_input(
                "Entity " + std::to_string(i) + " is not an edge, so it cannot be blended.");
        }

        selected.emplace_back(TopoDS::Edge(entity), values[i]);
    }

    return selected;
}

/*
 * Records a blend face as generated from the two faces it runs between, as well as from the edge
 * it replaced.
 *
 * The edge relationship alone is not enough to name a blend. The edge is gone, so a reference to
 * "the fillet on that edge" has nothing to re-resolve against on the next rebuild -- whereas the
 * two faces it ran between are still there, and "the blend between the top and the front" survives
 * the edge being renumbered, the box being resized, or another feature being inserted before it.
 * ADR-0005 calls this out as the case that makes or breaks a naming scheme.
 *
 * Both relationships are recorded, not one: the edge link is what an undo or a diff wants, and the
 * face link is what a name wants.
 */
void mapBlendToNeighbours(
    HistoryRecord& record,
    BRepBuilderAPI_MakeShape& builder,
    const std::vector<std::pair<TopoDS_Edge, double>>& edges,
    const TopoDS_Shape& before,
    ShapeRef beforeRef,
    const TopoDS_Shape& after,
    ShapeRef afterRef,
    Role role)
{
    ShapeAncestorMap edgeToFaces;
    TopExp::MapShapesAndAncestors(before, TopAbs_EDGE, TopAbs_FACE, edgeToFaces);

    ShapeIndexedMap present;
    TopExp::MapShapes(after, TopAbs_FACE, present);

    for (const auto& entry : edges)
    {
        if (!edgeToFaces.Contains(entry.first))
        {
            continue;
        }

        for (const TopoDS_Shape& made : builder.Generated(entry.first))
        {
            if (made.ShapeType() != TopAbs_FACE || !present.Contains(made))
            {
                continue;
            }

            const uint64_t blendTag = handles().store_entity(afterRef, made).tag;

            for (const TopoDS_Shape& neighbour : edgeToFaces.FindFromKey(entry.first))
            {
                record.add_generated(
                    handles().store_entity(beforeRef, neighbour).tag,
                    blendTag,
                    static_cast<int32_t>(role));
            }
        }
    }
}

/* Shared tail of fillet and chamfer. */
void completeBlend(
    BRepBuilderAPI_MakeShape& builder,
    const std::vector<std::pair<TopoDS_Edge, double>>& edges,
    Role blendRole,
    const TopoDS_Shape& before,
    ShapeRef bodyRef,
    ShapeOut result,
    HistoryOut history)
{
    const TopoDS_Shape built = builder.Shape();
    if (built.IsNull())
    {
        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, "The blend produced no geometry.");
    }

    // A blend that overruns its neighbours produces a self-intersecting body that OCCT still
    // reports as done. Catching it here turns a corrupt model into a failed feature, which the
    // rebuild can recover from.
    if (!BRepCheck_Analyzer(built).IsValid())
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE,
            "The blend produced an invalid body. The radius is probably too large for the "
            "geometry it has to fit into.");
    }

    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    std::vector<std::pair<TopoDS_Shape, ShapeRef>> inputs;
    inputs.emplace_back(before, bodyRef);

    mapAll(
        *record, builder, inputs, built, shape,
        blendRole, Role::Trimmed,
        FallbackRoles{Role::BlendCornerFace, Role::BlendEdge, Role::IntersectionVertex});

    mapBlendToNeighbours(*record, builder, edges, before, bodyRef, built, shape, blendRole);

    result.set(shape);
    history.set(handles().store(std::move(record)));
}

} /* namespace */

void fillet(
    ShapeRef body, std::span<const uint64_t> edges, std::span<const double> radii,
    double tolerance, ShapeOut result, HistoryOut history, int32_t& rung)
{
    const TopoDS_Shape& before = handles().resolve(body);
    const std::vector<std::pair<TopoDS_Edge, double>> selected =
        blendInputs(body, edges, radii, "radii");

    BRepFilletAPI_MakeFillet builder(before);
    for (const auto& entry : selected)
    {
        builder.Add(entry.second, entry.first);
    }

    builder.Build();
    if (!builder.IsDone())
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE,
            "The fillet could not be built. The radius may exceed the space available at one of "
            "the edges, or the selected edges may meet in a corner the blender cannot resolve.");
    }

    // The ladder is P1-T11. One attempt, at model tolerance, and it worked.
    rung = static_cast<int32_t>(Rung::ModelTolerance);
    (void)tolerance;

    completeBlend(builder, selected, Role::BlendFace, before, body, result, history);
}

void chamfer(
    ShapeRef body, std::span<const uint64_t> edges, std::span<const double> distances,
    double tolerance, ShapeOut result, HistoryOut history, int32_t& rung)
{
    const TopoDS_Shape& before = handles().resolve(body);
    const std::vector<std::pair<TopoDS_Edge, double>> selected =
        blendInputs(body, edges, distances, "distances");

    // A chamfer is measured from one of the two faces the edge divides, and OCCT wants to be told
    // which. The ancestor map is built once rather than per edge: it is a full traversal.
    ShapeAncestorMap edgeToFaces;
    TopExp::MapShapesAndAncestors(before, TopAbs_EDGE, TopAbs_FACE, edgeToFaces);

    // Canonical face order, computed once, so the reference face below is chosen the same way on
    // every rebuild. Picking whichever face traversal happened to yield first would make the
    // setback direction depend on traversal order and break ADR-0011.
    const std::vector<TopoDS_Shape> canonicalFaces = enumerate_canonical(before, TopAbs_FACE);

    BRepFilletAPI_MakeChamfer builder(before);
    for (const auto& entry : selected)
    {
        if (!edgeToFaces.Contains(entry.first))
        {
            throw invalid_input("A selected edge bounds no face, so it cannot be chamfered.");
        }

        const ShapeList& faces = edgeToFaces.FindFromKey(entry.first);
        if (faces.IsEmpty())
        {
            throw invalid_input("A selected edge bounds no face, so it cannot be chamfered.");
        }

        TopoDS_Shape reference;
        for (const TopoDS_Shape& face : canonicalFaces)
        {
            bool bounds = false;
            for (const TopoDS_Shape& candidate : faces)
            {
                bounds = bounds || candidate.IsSame(face);
            }

            if (bounds)
            {
                reference = face;
                break;
            }
        }

        if (reference.IsNull())
        {
            throw kernel_error(
                OPENMCAD_ERROR_KERNEL_FAILURE,
                "A selected edge's adjacent faces are not part of the body being chamfered.");
        }

        // Both distances equal: a symmetric 45-degree chamfer. The face still has to be passed,
        // because it is what the distances are measured from -- and choosing it canonically above
        // is what keeps an asymmetric chamfer's direction stable across rebuilds.
        builder.Add(entry.second, entry.second, entry.first, TopoDS::Face(reference));
    }

    builder.Build();
    if (!builder.IsDone())
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE,
            "The chamfer could not be built. The setback may exceed the space available at one of "
            "the edges.");
    }

    rung = static_cast<int32_t>(Rung::ModelTolerance);
    (void)tolerance;

    // SetbackFace, not BlendFace: a chamfer face is planar and a fillet face is not, and a
    // downstream selection that asks for "the rounded faces" must not pick up chamfers.
    completeBlend(builder, selected, Role::SetbackFace, before, body, result, history);
}

} /* namespace openmcad::ops */

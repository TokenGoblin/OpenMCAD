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
#include <Precision.hxx>
#include <Standard_Failure.hxx>
#include <gp.hxx>
#include <gp_Ax1.hxx>
#include <gp_Vec.hxx>

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ladder.h"
#include "openmcad_ops.g.h"
#include "openmcad_roles.h"

namespace openmcad::ops {

namespace {

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
    Role modifiedRole,
    const Conditioned& correspondence)
{
    for (const TopoDS_Shape& input : enumerate_canonical(before, kind))
    {
        const uint64_t inputTag = handles().store_entity(beforeRef, input).tag;

        // The builder saw the conditioned shape, if conditioning ran. The caller holds handles to
        // the original, so the tag comes from the original and the question goes to its
        // counterpart -- otherwise a rung-2 result reports an empty history and every entity in it
        // looks newly created.
        const TopoDS_Shape& asked = correspondence.of(input);

        for (const TopoDS_Shape& made : builder.Generated(asked))
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

        for (const TopoDS_Shape& changed : builder.Modified(asked))
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
        if (builder.IsDeleted(asked))
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
    FallbackRoles fallback,
    const Conditioned& correspondence)
{
    const ShapeIndexedMap present = nameableEntities(after);

    for (TopAbs_ShapeEnum kind : kMappedKinds)
    {
        for (const auto& entry : inputs)
        {
            mapReported(
                record, builder, entry.first, entry.second, present, afterRef, kind,
                generatedRole, modifiedRole, correspondence);

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
            Role::Transformed, Conditioned{});

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

    // Resolve every handle up front, so a bad tag fails before any modelling work is done.
    (void)handles().resolve(target);
    for (uint64_t tag : tools)
    {
        (void)handles().resolve(ShapeRef{tag});
    }

    if (operation < 0 || operation > 2)
    {
        throw invalid_input(
            "Unknown boolean operation " + std::to_string(operation)
            + ". Expected 0 union, 1 subtract, or 2 intersect.");
    }

    // The ladder (PLAN.md 5.2.4). Each rung is a complete attempt, and the loop exists so that
    // adding a rung later is adding an entry rather than another level of nesting.
    const Rung ladder[] = {Rung::ModelTolerance, Rung::Conditioned, Rung::FuzzyTolerance};

    std::unique_ptr<BRepAlgoAPI_BooleanOperation> builder;
    Conditioned targetConditioned;
    std::vector<Conditioned> toolsConditioned;
    TopoDS_Shape built;
    Rung succeeded = Rung::NotApplicable;
    std::string lastReason;

    for (Rung attempt : ladder)
    {
        if (attempt == Rung::Conditioned)
        {
            targetConditioned = condition(handles().resolve(target));

            toolsConditioned.clear();
            toolsConditioned.reserve(tools.size());

            bool repaired = targetConditioned.changed;
            for (uint64_t tag : tools)
            {
                toolsConditioned.push_back(condition(handles().resolve(ShapeRef{tag})));
                repaired = repaired || toolsConditioned.back().changed;
            }

            // Conditioning that changed nothing cannot change the outcome. Retrying an identical
            // attempt to obtain an identical failure only spends the user's time.
            if (!repaired)
            {
                note_rung_failed(
                    "The boolean operation", attempt, "conditioning found nothing to repair");
                continue;
            }
        }

        switch (operation)
        {
            case 0:  builder = std::make_unique<BRepAlgoAPI_Fuse>(); break;
            case 1:  builder = std::make_unique<BRepAlgoAPI_Cut>(); break;
            default: builder = std::make_unique<BRepAlgoAPI_Common>(); break;
        }

        ShapeList arguments;
        ShapeList toolShapes;

        if (attempt == Rung::Conditioned)
        {
            arguments.Append(targetConditioned.shape);
            for (const Conditioned& tool : toolsConditioned)
            {
                toolShapes.Append(tool.shape);
            }
        }
        else
        {
            arguments.Append(handles().resolve(target));
            for (uint64_t tag : tools)
            {
                toolShapes.Append(handles().resolve(ShapeRef{tag}));
            }
        }

        builder->SetArguments(arguments);
        builder->SetTools(toolShapes);

        // ADR-0011. OCCT's parallel boolean partitions work across threads, and the order faces
        // are merged in can change which of several tolerance-equal results comes out.
        // Determinism is worth more here than the throughput, and ADR-0004 already confines the
        // kernel to a single thread.
        builder->SetRunParallel(false);

        // The inputs are still owned by the handle table and may be referenced by another feature
        // in the tree. Letting OCCT modify them in place would corrupt the history of operations
        // that have already run.
        builder->SetNonDestructive(true);

        if (tolerance > 0.0)
        {
            builder->SetFuzzyValue(tolerance);
        }

        if (attempt == Rung::FuzzyTolerance)
        {
            // Relaxed by a defined factor of the model tolerance, not by whatever makes the
            // failure go away. A fuzzy value large enough to fix anything is also large enough to
            // merge two features the user meant to keep apart, and that result looks plausible
            // while being wrong -- the outcome PLAN.md 6.1 exists to prevent. A caller who named a
            // value has already made that judgement, so theirs is used instead.
            const double base = tolerance > 0.0 ? tolerance : Precision::Confusion();
            builder->SetFuzzyValue(fuzzy_tolerance > 0.0 ? fuzzy_tolerance : base * 100.0);
        }

        builder->Build();

        if (builder->HasErrors())
        {
            std::ostringstream detail;
            builder->DumpErrors(detail);
            lastReason = detail.str();
            note_rung_failed("The boolean operation", attempt, lastReason);
            continue;
        }

        if (builder->Shape().IsNull())
        {
            lastReason = "the operation produced no geometry";
            note_rung_failed("The boolean operation", attempt, lastReason);
            continue;
        }

        built = builder->Shape();
        succeeded = attempt;
        break;
    }

    if (succeeded == Rung::NotApplicable)
    {
        // Rung 5. The message names what was tried and what to change, because "the boolean
        // failed" leaves the user with nowhere to go.
        std::string message =
            "The boolean operation failed at every stage: at model tolerance, after repairing the "
            "input bodies, and at a relaxed tolerance.";

        if (!lastReason.empty())
        {
            message += " The kernel reported: " + lastReason;
        }

        message +=
            " The bodies may not intersect at all, or they may touch exactly along a face, which "
            "is ambiguous. Moving one body slightly so the overlap is unambiguous usually resolves "
            "it.";

        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, message);
    }

    rung = static_cast<int32_t>(succeeded);

    // History is mapped against the shapes the caller holds handles to, whichever rung produced
    // the result. The correspondence carries the translation when conditioning ran.
    std::vector<std::pair<TopoDS_Shape, ShapeRef>> inputs;
    inputs.emplace_back(handles().resolve(target), target);
    for (uint64_t tag : tools)
    {
        const ShapeRef toolRef{tag};
        inputs.emplace_back(handles().resolve(toolRef), toolRef);
    }

    Conditioned correspondence;
    if (succeeded == Rung::Conditioned)
    {
        correspondence = targetConditioned;
        for (const Conditioned& tool : toolsConditioned)
        {
            // OCCT's own iterator, not the STL-compatible one: the latter yields values only, and
            // merging two correspondences needs the keys.
            for (ShapeImageMap::Iterator it(tool.image); it.More(); it.Next())
            {
                correspondence.image.Bind(it.Key(), it.Value());
            }
        }
    }

    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    mapAll(
        *record, *builder, inputs, built, shape,
        Role::SplitPositive, Role::Trimmed,
        FallbackRoles{Role::CoincidentFace, Role::IntersectionEdge, Role::IntersectionVertex},
        correspondence);

    result.set(shape);
    history.set(handles().store(std::move(record)));
}

namespace {

/*
 * Shared front half of fillet and chamfer: both take the same edge/value pairing, and both have to
 * reject the same mistakes.
 */
/* One selected edge: what the caller called it, which edge it is, and how much to take off. */
struct BlendEdge
{
    uint64_t tag;
    TopoDS_Edge edge;
    double value;
};

std::vector<BlendEdge> blendInputs(
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

    std::vector<BlendEdge> selected;
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

        selected.push_back(BlendEdge{edges[i], TopoDS::Edge(entity), values[i]});
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
    const std::vector<BlendEdge>& edges,
    const TopoDS_Shape& before,
    ShapeRef beforeRef,
    const TopoDS_Shape& after,
    ShapeRef afterRef,
    const Conditioned& correspondence,
    Role role)
{
    ShapeAncestorMap edgeToFaces;
    TopExp::MapShapesAndAncestors(before, TopAbs_EDGE, TopAbs_FACE, edgeToFaces);

    ShapeIndexedMap present;
    TopExp::MapShapes(after, TopAbs_FACE, present);

    for (const BlendEdge& entry : edges)
    {
        if (!edgeToFaces.Contains(entry.edge))
        {
            continue;
        }

        for (const TopoDS_Shape& made : builder.Generated(correspondence.of(entry.edge)))
        {
            if (made.ShapeType() != TopAbs_FACE || !present.Contains(made))
            {
                continue;
            }

            const uint64_t blendTag = handles().store_entity(afterRef, made).tag;

            for (const TopoDS_Shape& neighbour : edgeToFaces.FindFromKey(entry.edge))
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
    const TopoDS_Shape& built,
    const std::vector<BlendEdge>& edges,
    Role blendRole,
    const TopoDS_Shape& before,
    ShapeRef bodyRef,
    const Conditioned& correspondence,
    ShapeOut result,
    HistoryOut history)
{
    const ShapeRef shape = handles().store(built);
    auto record = std::make_unique<HistoryRecord>();

    std::vector<std::pair<TopoDS_Shape, ShapeRef>> inputs;
    inputs.emplace_back(before, bodyRef);

    mapAll(
        *record, builder, inputs, built, shape,
        blendRole, Role::Trimmed,
        FallbackRoles{Role::BlendCornerFace, Role::BlendEdge, Role::IntersectionVertex},
        correspondence);

    mapBlendToNeighbours(
        *record, builder, edges, before, bodyRef, built, shape, correspondence, blendRole);

    result.set(shape);
    history.set(handles().store(std::move(record)));
}


/*
 * One blend attempt: build the given subset of edges against the given body.
 *
 * Returns false rather than throwing, because every caller here is a rung that has somewhere else
 * to go. The checks are deliberately strict -- IsDone alone is not enough, since a blend that
 * overruns its neighbours produces a self-intersecting body that OCCT still reports as done, and
 * a corrupt body is a worse outcome than a failed feature.
 */
template <typename Builder, typename Adder>
bool attemptBlend(
    const TopoDS_Shape& body,
    const std::vector<BlendEdge>& edges,
    const std::vector<size_t>& which,
    const Conditioned& correspondence,
    Adder&& add,
    std::unique_ptr<Builder>& builder,
    TopoDS_Shape& built,
    std::string& reason)
{
    if (which.empty())
    {
        reason = "no edges to apply";
        return false;
    }

    try
    {
        builder = std::make_unique<Builder>(body);

        for (size_t index : which)
        {
            const TopoDS_Shape& edge = correspondence.of(edges[index].edge);

            // Conditioning can remove an edge outright -- that is the point of it. An edge that no
            // longer exists cannot be blended, and saying so is better than blending the wrong one.
            if (edge.IsNull() || edge.ShapeType() != TopAbs_EDGE)
            {
                reason = "a selected edge did not survive input conditioning";
                return false;
            }

            add(*builder, body, TopoDS::Edge(edge), edges[index].value);
        }

        builder->Build();
    }
    catch (const Standard_Failure& failure)
    {
        reason = failure.what();
        return false;
    }

    if (!builder->IsDone())
    {
        reason = "the blend algorithm did not converge";
        return false;
    }

    built = builder->Shape();
    if (built.IsNull())
    {
        reason = "the blend produced no geometry";
        return false;
    }

    if (!BRepCheck_Analyzer(built).IsValid())
    {
        reason = "the blend produced a self-intersecting body";
        return false;
    }

    return true;
}

/*
 * The blend ladder (PLAN.md 5.2.4, rungs 1, 2, 4 and 5).
 *
 * Rung 3 has no meaning here: a relaxed fuzzy tolerance is a boolean concept, and
 * BRepFilletAPI has no equivalent knob. Rung 4 is where a blend earns the ladder -- one edge in a
 * selection of twelve being impossible should cost the user that edge, not the whole feature.
 *
 * Rung 4 accumulates rather than bisecting: edges are added one at a time and kept if the build
 * still succeeds. That is n builds rather than the n-squared of retrying every subset, and it
 * catches interactions as well as individually impossible edges -- an edge that only fails in the
 * presence of another is dropped when its turn comes.
 */
template <typename Builder, typename Adder>
void runBlendLadder(
    const char* operation,
    ShapeRef body,
    const std::vector<BlendEdge>& edges,
    Adder&& add,
    Role blendRole,
    ShapeOut result,
    HistoryOut history,
    int32_t& rung)
{
    const TopoDS_Shape original = handles().resolve(body);

    std::vector<size_t> all(edges.size());
    for (size_t i = 0; i < edges.size(); ++i)
    {
        all[i] = i;
    }

    std::unique_ptr<Builder> builder;
    TopoDS_Shape built;
    std::string reason;
    Conditioned correspondence;

    // Rung 1.
    if (attemptBlend(original, edges, all, correspondence, add, builder, built, reason))
    {
        rung = static_cast<int32_t>(Rung::ModelTolerance);
        completeBlend(
            *builder, built, edges, blendRole, original, body, correspondence, result, history);
        return;
    }

    note_rung_failed(operation, Rung::ModelTolerance, reason);

    // Rung 2.
    Conditioned conditioned = condition(original);
    if (conditioned.changed
        && attemptBlend(conditioned.shape, edges, all, conditioned, add, builder, built, reason))
    {
        rung = static_cast<int32_t>(Rung::Conditioned);
        completeBlend(
            *builder, built, edges, blendRole, original, body, conditioned, result, history);
        return;
    }

    note_rung_failed(
        operation,
        Rung::Conditioned,
        conditioned.changed ? reason : "conditioning found nothing to repair");

    // Rung 4. Back to the original body: conditioning did not help, and keeping it would rename
    // entities for no benefit.
    std::vector<size_t> kept;
    std::vector<uint64_t> refused;

    std::unique_ptr<Builder> partialBuilder;
    TopoDS_Shape partialBuilt;

    for (size_t i = 0; i < edges.size(); ++i)
    {
        std::vector<size_t> candidate = kept;
        candidate.push_back(i);

        std::unique_ptr<Builder> attempt;
        TopoDS_Shape shape;
        std::string ignored;

        if (attemptBlend(original, edges, candidate, correspondence, add, attempt, shape, ignored))
        {
            kept = std::move(candidate);
            partialBuilder = std::move(attempt);
            partialBuilt = shape;
        }
        else
        {
            refused.push_back(edges[i].tag);
        }
    }

    if (kept.empty())
    {
        // Rung 5. Every edge failed, so the message is about the operation rather than a subset.
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE,
            std::string(operation)
            + " could not be applied to any of the selected edges, at model tolerance, after "
              "repairing the body, or one edge at a time. The kernel reported: " + reason
            + ". The value is most likely larger than the material available at these edges; try a "
              "smaller one, or apply the blend before the feature that removed the material.");
    }

    // Degraded: some of what was asked for, and the caller is told exactly what was left out.
    // Warning severity is what the managed layer keys Degraded off, so this is the signal as well
    // as the explanation.
    std::string message =
        std::string(operation) + " was applied to " + std::to_string(kept.size()) + " of "
        + std::to_string(edges.size()) + " selected edges. The remaining "
        + std::to_string(refused.size())
        + " could not be blended at the requested size -- there is not enough material at them, or "
          "they meet in a corner the blender cannot resolve. Reduce the value on those edges, or "
          "deselect them.";

    report(1, "OMK3001", message, refused);

    rung = static_cast<int32_t>(Rung::ModelTolerance);
    completeBlend(
        *partialBuilder, partialBuilt, edges, blendRole, original, body, correspondence,
        result, history);
}

} /* namespace */

void fillet(
    ShapeRef body, std::span<const uint64_t> edges, std::span<const double> radii,
    double tolerance, ShapeOut result, HistoryOut history, int32_t& rung)
{
    (void)tolerance;

    runBlendLadder<BRepFilletAPI_MakeFillet>(
        "The fillet",
        body,
        blendInputs(body, edges, radii, "radii"),
        [](BRepFilletAPI_MakeFillet& builder, const TopoDS_Shape&, const TopoDS_Edge& edge,
           double radius) { builder.Add(radius, edge); },
        Role::BlendFace,
        result,
        history,
        rung);
}

void chamfer(
    ShapeRef body, std::span<const uint64_t> edges, std::span<const double> distances,
    double tolerance, ShapeOut result, HistoryOut history, int32_t& rung)
{
    (void)tolerance;

    runBlendLadder<BRepFilletAPI_MakeChamfer>(
        "The chamfer",
        body,
        blendInputs(body, edges, distances, "distances"),
        [](BRepFilletAPI_MakeChamfer& builder, const TopoDS_Shape& shape, const TopoDS_Edge& edge,
           double distance)
        {
            // A chamfer is measured from one of the two faces the edge divides, and OCCT wants to
            // be told which. Canonical order picks it, so an asymmetric chamfer measures from the
            // same side on every rebuild rather than from whichever face traversal yielded first.
            ShapeAncestorMap edgeToFaces;
            TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, edgeToFaces);

            if (!edgeToFaces.Contains(edge))
            {
                throw invalid_input("A selected edge bounds no face, so it cannot be chamfered.");
            }

            const ShapeList& faces = edgeToFaces.FindFromKey(edge);
            TopoDS_Shape reference;

            for (const TopoDS_Shape& face : enumerate_canonical(shape, TopAbs_FACE))
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
                throw invalid_input("A selected edge bounds no face, so it cannot be chamfered.");
            }

            // Both distances equal: a symmetric 45-degree chamfer. The face still has to be
            // passed, because it is what the distances are measured from.
            builder.Add(distance, distance, edge, TopoDS::Face(reference));
        },
        Role::SetbackFace,
        result,
        history,
        rung);
}

} /* namespace openmcad::ops */

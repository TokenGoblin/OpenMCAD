/*
 * openmcad_ladder.h - the operation retry ladder (P1-T11, PLAN.md 5.2.4).
 *
 * ADR-0001 names boolean and blend robustness as the known weak point of OCCT: tangent faces,
 * near-coincident geometry and self-intersecting fillet chains fail in ways Parasolid does not.
 * The mitigation is not to hope, but to escalate:
 *
 *   1. ModelTolerance  attempt as asked
 *   2. Conditioned     repair the inputs (sew, drop tiny edges, unify same-domain faces), retry
 *   3. FuzzyTolerance  retry with a relaxed tolerance, for booleans only
 *   4. (blends)        retry edge by edge to isolate the failing subset, return Degraded
 *   5. Failed          with a message naming the operation, the entities and what was tried
 *
 * Every rung that fires is logged as an Information diagnostic, and the rung that finally
 * succeeded is reported back on the result. PLAN.md 5.2.4 asks for the distribution of those to be
 * tracked across the corpus over time: if the rung-1 success rate falls, something regressed.
 *
 * The ladder is here rather than in the managed layer because rungs 2 and 3 need OCCT itself --
 * ShapeFix, ShapeUpgrade and SetFuzzyValue have no equivalent across the C ABI, and shipping
 * conditioned geometry back and forth to retry would cost more than the operation.
 */

#ifndef OPENMCAD_LADDER_H
#define OPENMCAD_LADDER_H

#include <cstdint>
#include <string>

#include <NCollection_DataMap.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <TopoDS_Shape.hxx>

#include "openmcad_types.h"

namespace openmcad {

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

const char* to_string(Rung rung) noexcept;

/* An entity of an original shape, mapped to its counterpart in a conditioned copy. */
using ShapeImageMap = NCollection_DataMap<TopoDS_Shape, TopoDS_Shape, TopTools_ShapeMapHasher>;

/*
 * A conditioned copy of an input, and the correspondence back to the original.
 *
 * The correspondence is the whole difficulty. Conditioning rebuilds topology -- it merges
 * same-domain faces and drops edges below tolerance -- so the shape the builder saw on rung 2 is
 * not the shape the caller holds handles to. Asking the builder what it did with an entity the
 * caller knows about therefore needs a translation, or every Generated and Modified relationship
 * silently comes back empty and the operation's history collapses to "everything is new".
 *
 * `image` maps an entity of the original to its counterpart in the conditioned shape. An entity
 * that conditioning removed outright has no image, and the caller treats that as it treats any
 * other input with nothing to say about it: the retained sweep looks it up in the result and
 * settles it.
 */
struct Conditioned
{
    TopoDS_Shape shape;
    ShapeImageMap image;

    /* The conditioned counterpart of an original entity, or the entity itself if unchanged. */
    const TopoDS_Shape& of(const TopoDS_Shape& original) const;

    /* Whether conditioning actually changed anything. */
    bool changed = false;
};

/*
 * Repairs an input shape: fixes invalid topology, removes edges below tolerance, and unifies
 * faces that lie on the same underlying surface.
 *
 * This is rung 2, and it is worth its cost only because the failures it addresses are so common:
 * a boolean against a body that was itself produced by a boolean often fails on seams that are
 * geometrically redundant but topologically present. Unifying them removes the failure's cause
 * rather than papering over it with tolerance, which is why this rung comes before the fuzzy one.
 *
 * Never applied speculatively. Conditioning a healthy body changes its topology, which renames
 * entities for no benefit -- so it runs only after an honest attempt has already failed.
 */
Conditioned condition(const TopoDS_Shape& input);

/*
 * Records that a rung was tried and did not work.
 *
 * Information severity, not Warning: an operation that succeeds on rung 2 succeeded, and the user
 * has nothing to act on. The record exists for the health metric and for a support bundle, so it
 * names the rung and OCCT's own reason rather than being a message written for a person.
 */
void note_rung_failed(const char* operation, Rung rung, const std::string& reason);

} /* namespace openmcad */

#endif /* OPENMCAD_LADDER_H */

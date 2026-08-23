/*
 * openmcad_canonical.h - canonical entity ordering, and the retained-entity sweep.
 *
 * Two things every operation body needs, factored out so they are written once rather than once
 * per operation, slightly differently each time.
 */

#ifndef OPENMCAD_CANONICAL_H
#define OPENMCAD_CANONICAL_H

#include <set>
#include <vector>

#include <TopAbs_ShapeEnum.hxx>
#include <TopoDS_Shape.hxx>

#include "openmcad_handles.h"

namespace openmcad {

/*
 * Returns a shape's entities of one kind, in a canonical, geometry-derived order.
 *
 * The OCCT spike found TopExp_Explorer's order to be stable in practice -- two identically-built
 * boxes traversed identically, and repeated traversal of one shape agreed across twenty runs. It
 * is nonetheless sorted here, because PLAN.md 5.2.3 asks for "a stable geometric key" and
 * empirical stability is not a contract. Traversal order is treated as a good starting point that
 * this function confirms, not as the answer.
 *
 * The key is measure, then centroid, then a direction, all rounded to a tolerance well above
 * floating-point noise but well below any real feature size. Ties beyond that are broken by
 * traversal order, which keeps the result total.
 */
std::vector<TopoDS_Shape> enumerate_canonical(const TopoDS_Shape& shape, TopAbs_ShapeEnum kind);

/*
 * Tags a shape's entities of one kind in canonical order.
 */
std::vector<uint64_t> tag_canonical(ShapeRef owner, TopAbs_ShapeEnum kind);

/*
 * Records provenance for every input entity the kernel said nothing about.
 *
 * The OCCT spike's most consequential finding: cutting a cylinder from a box, OCCT reported two of
 * six target faces as modified, none as deleted, and nothing whatever about the other four.
 * OperationRole::Retained -- the majority of any boolean -- cannot be read from the history at all.
 *
 * So the survivors are found here instead. OCCT keeps the same TShape for an untouched entity, so
 * looking the input up in the output settles it:
 *
 *     found     -> Retained, mapped to the survivor
 *     not found -> deleted
 *
 * The lookup is the authority, deliberately. Writing "no history entry and not IsDeleted implies
 * retained" would classify a genuinely dropped entity as retained, and then there is no output
 * entity to point at.
 *
 * @param record     the provenance being accumulated
 * @param before     the operation's input shape
 * @param beforeRef  its handle, for tagging input entities
 * @param after      the operation's output shape
 * @param afterRef   its handle, for tagging output entities
 * @param kind       which entity kind to sweep
 */
void sweep_retained(
    HistoryRecord& record,
    const TopoDS_Shape& before,
    ShapeRef beforeRef,
    const TopoDS_Shape& after,
    ShapeRef afterRef,
    TopAbs_ShapeEnum kind);


/*
 * Records every output entity that no input accounted for.
 *
 * The counterpart to sweep_retained, and required for the same reason. PLAN.md 5.1: an operation
 * that returns entities its history does not describe is an incomplete implementation, and the
 * managed HistoryMapBuilder refuses one. A boolean invents intersection edges that came from no
 * single input; a fillet invents corner faces where three blends meet. Those are genuinely new,
 * so they are recorded as created rather than left unexplained.
 *
 * Called last, after generated/modified/deleted and after sweep_retained, so that "unclaimed"
 * means unclaimed by anything.
 */
void sweep_created(
    HistoryRecord& record,
    ShapeRef afterRef,
    TopAbs_ShapeEnum kind,
    int32_t role);

} /* namespace openmcad */

#endif /* OPENMCAD_CANONICAL_H */

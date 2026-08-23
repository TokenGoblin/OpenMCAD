#include "openmcad_ladder.h"

#include <BRepBuilderAPI_Copy.hxx>
#include <BRepTools_History.hxx>
#include <ShapeBuild_ReShape.hxx>
#include <ShapeFix_Shape.hxx>
#include <ShapeUpgrade_UnifySameDomain.hxx>
#include <Precision.hxx>
#include <Standard_Failure.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <TopExp.hxx>

#include "openmcad_handles.h"

namespace openmcad {

namespace {

/* The entity kinds a correspondence has to cover: the ones a name can refer to. */
constexpr TopAbs_ShapeEnum kNameableKinds[] = {TopAbs_FACE, TopAbs_EDGE, TopAbs_VERTEX};

/*
 * Follows one entity through a repair step.
 *
 * ShapeFix reports its work through a ShapeBuild_ReShape context, which answers "what replaced
 * this?". A null answer means unchanged, not removed -- BRepTools_ReShape only records the
 * entities it actually touched.
 */
TopoDS_Shape throughReShape(const Handle(ShapeBuild_ReShape)& context, const TopoDS_Shape& entity)
{
    if (context.IsNull())
    {
        return entity;
    }

    const TopoDS_Shape replacement = context->Value(entity);
    return replacement.IsNull() ? entity : replacement;
}

/*
 * Follows one entity through the unification step.
 *
 * Unification merges faces, so an entity can have several images or none. Several is not useful
 * to a correspondence that has to answer with one entity, and it happens precisely when the thing
 * the caller named has been split -- so the first is taken and the ambiguity is left to the
 * retained sweep, which resolves against the result rather than guessing here.
 */
TopoDS_Shape throughHistory(const Handle(BRepTools_History)& history, const TopoDS_Shape& entity)
{
    if (history.IsNull())
    {
        return entity;
    }

    if (history->IsRemoved(entity))
    {
        return TopoDS_Shape();
    }

    const ShapeList& modified = history->Modified(entity);
    return modified.IsEmpty() ? entity : modified.First();
}

} /* namespace */

const char* to_string(Rung rung) noexcept
{
    switch (rung)
    {
        case Rung::ModelTolerance: return "model tolerance";
        case Rung::Conditioned:    return "conditioned inputs";
        case Rung::FuzzyTolerance: return "relaxed tolerance";
        default:                   return "not applicable";
    }
}

const TopoDS_Shape& Conditioned::of(const TopoDS_Shape& original) const
{
    const TopoDS_Shape* found = image.Seek(original);
    return found != nullptr ? *found : original;
}

Conditioned condition(const TopoDS_Shape& input)
{
    Conditioned result;
    result.shape = input;

    if (input.IsNull())
    {
        return result;
    }

    Handle(ShapeBuild_ReShape) context;
    Handle(BRepTools_History) unified;
    BRepBuilderAPI_Copy copier;

    try
    {
        // Conditioning runs on a deep copy, never on the caller's shape. ShapeFix repairs by
        // building a replacement, but it also raises sub-shape tolerances in place -- and the
        // input is still owned by the handle table and may be referenced by another feature in the
        // tree. Widening its tolerances as a side effect of a retry that might not even help would
        // silently change the results of operations that have already run.
        copier.Perform(input);
        if (!copier.IsDone())
        {
            return Conditioned{input, {}, false};
        }

        ShapeFix_Shape fixer(copier.Shape());
        fixer.SetPrecision(Precision::Confusion());
        fixer.Perform();

        result.shape = fixer.Shape();
        context = fixer.Context();

        ShapeUpgrade_UnifySameDomain unifier(result.shape, true, true, false);
        unifier.SetSafeInputMode(true);
        unifier.Build();

        result.shape = unifier.Shape();
        unified = unifier.History();
    }
    catch (const Standard_Failure&)
    {
        // Conditioning is a recovery attempt, and a recovery attempt that itself fails must not
        // replace the original failure. Report the shape unchanged so the caller moves to the next
        // rung with the diagnosis it already had.
        return Conditioned{input, {}, false};
    }

    if (result.shape.IsNull())
    {
        return Conditioned{input, {}, false};
    }

    result.changed = !result.shape.IsSame(input);
    if (!result.changed)
    {
        return result;
    }

    // The correspondence, built once over every nameable entity rather than looked up on demand.
    // Three steps compose, in the order they ran: copy, then ShapeFix, then unification. Missing
    // any one of them leaves the caller's entities pointing at nothing, which reads downstream as
    // "the operation invented all of this geometry".
    for (TopAbs_ShapeEnum kind : kNameableKinds)
    {
        ShapeIndexedMap entities;
        TopExp::MapShapes(input, kind, entities);

        for (int i = 1; i <= entities.Extent(); ++i)
        {
            const TopoDS_Shape& original = entities(i);

            const ShapeList& copies = copier.Modified(original);
            const TopoDS_Shape copied = copies.IsEmpty() ? original : copies.First();

            const TopoDS_Shape fixed = throughReShape(context, copied);
            if (fixed.IsNull())
            {
                continue;
            }

            const TopoDS_Shape merged = throughHistory(unified, fixed);
            if (merged.IsNull() || merged.IsSame(original))
            {
                continue;
            }

            result.image.Bind(original, merged);
        }
    }

    return result;
}

void note_rung_failed(const char* operation, Rung rung, const std::string& reason)
{
    std::string message = std::string(operation) + " did not succeed at " + to_string(rung);
    if (!reason.empty())
    {
        message += ": " + reason;
    }

    message += ". Escalating.";

    // OMK3002 is "succeeded after retry" in the managed code list. Recording the escalation under
    // it keeps every rung event on one code, which is what makes the distribution aggregable.
    report(0, "OMK3002", message);
}

} /* namespace openmcad */

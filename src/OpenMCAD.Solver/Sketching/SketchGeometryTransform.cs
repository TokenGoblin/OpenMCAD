namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// Maps one piece of sketch geometry through a <see cref="SketchTransform"/> (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to point, line, circle and arc.</b> Ellipse, elliptical arc, parabola and hyperbola
/// each need their own angle handling worked out with the same care P4-T11 gave a circular edge
/// under reflection, and getting any of them wrong is silent geometric corruption rather than a
/// crash. Returning <see langword="null"/> for now — checked here rather than left for a caller to
/// discover — is the same choice P4-T11 made for a curve kind it does not yet project.
/// </para>
/// <para>
/// Returns a new entity carrying the <em>same</em> <see cref="SketchEntity.Id"/> as the one given —
/// this is "where does this geometry go", not "make a copy of it". A caller that wants a copy
/// applies <c>with { Id = ... }</c> to the result, which is what <see cref="SketchEdit.Duplicate"/>
/// does.
/// </para>
/// </remarks>
public static class SketchGeometryTransform
{
    /// <summary>Maps an entity's geometry through a transform.</summary>
    /// <param name="transform">The transform.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The transformed entity, or <see langword="null"/> if this build cannot transform its kind.</returns>
    public static SketchEntity? Apply(SketchTransform transform, SketchEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return entity switch
        {
            SketchPoint point => point with { Position = transform.Apply(point.Position) },

            SketchLine line => line with
            {
                Start = transform.Apply(line.Start),
                End = transform.Apply(line.End),
            },

            SketchCircle circle => circle with
            {
                Centre = transform.Apply(circle.Centre),
                Radius = circle.Radius * transform.Scale,
            },

            SketchArc arc => Arc(transform, arc),

            _ => null,
        };
    }

    private static SketchArc Arc(SketchTransform transform, SketchArc arc)
    {
        double start = transform.ApplyAngle(arc.StartAngle);
        double end = transform.ApplyAngle(arc.EndAngle);

        // A reflection reverses which way increasing angle turns (see SketchTransform.ApplyAngle),
        // and SketchArc can only ever describe an anticlockwise sweep from Start to End (P4-T03) --
        // so representing the same physical arc after a mirror means swapping which end is which,
        // the same conclusion P4-T11 reaches for a circular edge viewed from the reflected side.
        (double newStart, double newEnd) = transform.Reflected ? (end, start) : (start, end);

        return arc with
        {
            Centre = transform.Apply(arc.Centre),
            Radius = arc.Radius * transform.Scale,
            StartAngle = newStart,
            EndAngle = newEnd,
        };
    }
}

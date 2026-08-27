using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A sketch as the flat vector of numbers a solver works on, and the way back.
/// </summary>
/// <remarks>
/// <para>
/// P4-T02. The boundary §5.6 describes: "submit a parameter vector, an entity set, and a constraint
/// set". Every solver wants this — planegcs takes a vector of pointers to doubles — and the
/// translation happens here rather than in each solver, so that a second implementation cannot lay
/// the numbers out differently and quietly change which entity a constraint acts on.
/// </para>
/// <para>
/// The layout is by entity in sketch order, and within an entity in the order the record declares
/// its fields. That is not arbitrary tidiness: the vector's order decides the order of the columns
/// of the Jacobian, which decides which of several equally valid answers a least-squares step
/// finds. An order taken from a dictionary would make the same sketch solve differently on two
/// machines, which ADR-0011 does not allow.
/// </para>
/// <para>
/// Knots are not parameters. Moving a knot changes a spline's parameterisation rather than its
/// placement, no constraint acts on one, and including them would report degrees of freedom no
/// user could ever use up.
/// </para>
/// </remarks>
public sealed class SketchParameters
{
    private readonly ImmutableArray<SketchEntity> _entities;
    private readonly ImmutableArray<int> _offsets;

    private SketchParameters(
        ImmutableArray<SketchEntity> entities,
        ImmutableArray<int> offsets,
        ImmutableArray<double> values)
    {
        _entities = entities;
        _offsets = offsets;
        Values = values;
    }

    /// <summary>Gets the numbers, in solver order.</summary>
    public ImmutableArray<double> Values { get; }

    /// <summary>Gets how many numbers there are.</summary>
    public int Count => Values.Length;

    /// <summary>Flattens a sketch.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <returns>Its parameters.</returns>
    public static SketchParameters Of(Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        return Of(sketch.Entities);
    }

    /// <summary>Flattens some geometry.</summary>
    /// <param name="entities">The geometry.</param>
    /// <returns>Its parameters.</returns>
    public static SketchParameters Of(SketchEntitySet entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        ImmutableArray<int>.Builder offsets = ImmutableArray.CreateBuilder<int>(entities.Count);
        ImmutableArray<double>.Builder values = ImmutableArray.CreateBuilder<double>();

        foreach (SketchEntity entity in entities.Ordered)
        {
            offsets.Add(values.Count);
            Flatten(entity, values);
        }

        return new SketchParameters(entities.Ordered, offsets.ToImmutable(), values.ToImmutable());
    }

    /// <summary>Where an entity's numbers begin.</summary>
    /// <param name="entity">Which entity.</param>
    /// <returns>The index, or -1 if the entity is not here.</returns>
    public int OffsetOf(SketchEntityId entity)
    {
        for (int i = 0; i < _entities.Length; ++i)
        {
            if (_entities[i].Id == entity)
            {
                return _offsets[i];
            }
        }

        return -1;
    }

    /// <summary>Rebuilds the geometry from a vector of numbers.</summary>
    /// <param name="values">The numbers, in the same order they came out in.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentException">The vector is the wrong length.</exception>
    /// <remarks>
    /// The other half of the boundary, and the reason a solver hands back a sketch rather than a
    /// vector: scattering the numbers back is bookkeeping only this type should ever do.
    /// </remarks>
    public SketchEntitySet Scatter(ImmutableArray<double> values)
    {
        if (values.Length != Values.Length)
        {
            throw new ArgumentException(
                $"This sketch has {Values.Length} parameters and {values.Length} numbers were "
                + "given back, so they do not describe it.",
                nameof(values));
        }

        SketchEntitySet rebuilt = SketchEntitySet.Empty;

        for (int i = 0; i < _entities.Length; ++i)
        {
            rebuilt = rebuilt.With(Rebuild(_entities[i], values, _offsets[i]));
        }

        return rebuilt;
    }

    /// <summary>Rebuilds a whole sketch from a vector of numbers.</summary>
    /// <param name="sketch">The sketch the vector came from.</param>
    /// <param name="values">The numbers.</param>
    /// <returns>The sketch, moved.</returns>
    public Sketch Scatter(Sketch sketch, ImmutableArray<double> values)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        return sketch with { Entities = Scatter(values) };
    }

    /// <summary>Finds where a point reference sits in the vector.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>
    /// The indices of its X and Y, or null when the point is not a parameter of its entity — an
    /// arc's midpoint is computed from the arc, not stored.
    /// </returns>
    /// <remarks>
    /// Only the points an entity actually stores can be moved directly. Everything else is a
    /// function of them, and a constraint on one is a constraint on whatever it is computed from —
    /// which is why residuals are evaluated from rebuilt geometry rather than by poking at indices.
    /// </remarks>
    public (int X, int Y)? IndexOf(SketchPointRef reference)
    {
        int offset = OffsetOf(reference.Entity);

        if (offset < 0)
        {
            return null;
        }

        SketchEntity entity = _entities.First(e => e.Id == reference.Entity);

        return (entity, reference.Point) switch
        {
            (SketchPoint, _) => (offset, offset + 1),
            (SketchLine, EntityPoint.Start) => (offset, offset + 1),
            (SketchLine, EntityPoint.End) => (offset + 2, offset + 3),
            (SketchCircle or SketchArc or SketchEllipse or SketchEllipticalArc
                or SketchHyperbola, EntityPoint.Centre) => (offset, offset + 1),
            (SketchParabola, EntityPoint.Focus) => (offset + 2, offset + 3),
            _ => null,
        };
    }

    /// <summary>How many numbers it takes to place one entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>How many of the vector's numbers are its.</returns>
    /// <remarks>
    /// The one table. <see cref="Sketch.Freedom"/> is the sum of this over the sketch, and a second
    /// table written out beside it would agree on the day it was written and drift the first time
    /// an entity kind gained a parameter — which is a number the user is shown.
    /// </remarks>
    public static int WidthOf(SketchEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        ImmutableArray<double>.Builder values = ImmutableArray.CreateBuilder<double>();
        Flatten(entity, values);

        return values.Count;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Count} parameters over {_entities.Length} entities";

    private static void Flatten(SketchEntity entity, ImmutableArray<double>.Builder values)
    {
        switch (entity)
        {
            case SketchPoint point:
                Add(values, point.Position);
                break;

            case SketchLine line:
                Add(values, line.Start);
                Add(values, line.End);
                break;

            case SketchCircle circle:
                Add(values, circle.Centre);
                values.Add(circle.Radius);
                break;

            case SketchArc arc:
                Add(values, arc.Centre);
                values.Add(arc.Radius);
                values.Add(arc.StartAngle);
                values.Add(arc.EndAngle);
                break;

            case SketchEllipticalArc arc:
                Add(values, arc.Centre);
                values.Add(arc.MajorRadius);
                values.Add(arc.MinorRadius);
                values.Add(arc.Rotation);
                values.Add(arc.StartAngle);
                values.Add(arc.EndAngle);
                break;

            case SketchEllipse ellipse:
                Add(values, ellipse.Centre);
                values.Add(ellipse.MajorRadius);
                values.Add(ellipse.MinorRadius);
                values.Add(ellipse.Rotation);
                break;

            case SketchParabola parabola:
                Add(values, parabola.Vertex);
                Add(values, parabola.Focus);
                values.Add(parabola.StartParameter);
                values.Add(parabola.EndParameter);
                break;

            case SketchHyperbola hyperbola:
                Add(values, hyperbola.Centre);
                values.Add(hyperbola.MajorRadius);
                values.Add(hyperbola.MinorRadius);
                values.Add(hyperbola.Rotation);
                values.Add(hyperbola.StartParameter);
                values.Add(hyperbola.EndParameter);
                break;

            case SketchBSpline spline:
                foreach (Vec2d pole in spline.ControlPoints)
                {
                    Add(values, pole);
                }

                foreach (double weight in spline.PoleWeights)
                {
                    values.Add(weight);
                }

                break;

            default:
                throw new ArgumentException(
                    $"There is no way to flatten a {entity.Kind}, so no solver could move it.",
                    nameof(entity));
        }
    }

    private static SketchEntity Rebuild(
        SketchEntity entity, ImmutableArray<double> values, int at) => entity switch
    {
        SketchPoint point => point with { Position = Read(values, at) },

        SketchLine line => line with
        {
            Start = Read(values, at),
            End = Read(values, at + 2),
        },

        SketchCircle circle => circle with
        {
            Centre = Read(values, at),
            Radius = values[at + 2],
        },

        SketchArc arc => arc with
        {
            Centre = Read(values, at),
            Radius = values[at + 2],
            StartAngle = values[at + 3],
            EndAngle = values[at + 4],
        },

        SketchEllipticalArc arc => arc with
        {
            Centre = Read(values, at),
            MajorRadius = values[at + 2],
            MinorRadius = values[at + 3],
            Rotation = values[at + 4],
            StartAngle = values[at + 5],
            EndAngle = values[at + 6],
        },

        SketchEllipse ellipse => ellipse with
        {
            Centre = Read(values, at),
            MajorRadius = values[at + 2],
            MinorRadius = values[at + 3],
            Rotation = values[at + 4],
        },

        SketchParabola parabola => parabola with
        {
            Vertex = Read(values, at),
            Focus = Read(values, at + 2),
            StartParameter = values[at + 4],
            EndParameter = values[at + 5],
        },

        SketchHyperbola hyperbola => hyperbola with
        {
            Centre = Read(values, at),
            MajorRadius = values[at + 2],
            MinorRadius = values[at + 3],
            Rotation = values[at + 4],
            StartParameter = values[at + 5],
            EndParameter = values[at + 6],
        },

        SketchBSpline spline => spline with
        {
            Poles =
            [
                .. Enumerable.Range(0, spline.ControlPoints.Length)
                    .Select(i => Read(values, at + (i * 2))),
            ],
            Weights =
            [
                .. Enumerable.Range(0, spline.PoleWeights.Length)
                    .Select(i => values[at + (spline.ControlPoints.Length * 2) + i]),
            ],
        },

        _ => entity,
    };

    private static void Add(ImmutableArray<double>.Builder values, Vec2d point)
    {
        values.Add(point.X);
        values.Add(point.Y);
    }

    private static Vec2d Read(ImmutableArray<double> values, int at)
        => new(values[at], values[at + 1]);
}

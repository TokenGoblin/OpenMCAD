using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>Thrown when a sketch's text is not one.</summary>
public sealed class SketchFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public SketchFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public SketchFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with nothing to say.</summary>
    public SketchFormatException()
        : base("That is not a sketch.")
    {
    }
}

/// <summary>
/// Reads and writes a sketch as JSON.
/// </summary>
/// <remarks>
/// <para>
/// P4-T04. The interchange form: what a fixture in the sketch corpus (P4-T16) is written in, what a
/// test builds a sketch from, and what a bug report can be pasted into. It is deliberately readable
/// and diffable, which is what a corpus needs and what a binary payload cannot be.
/// </para>
/// <para>
/// <b>This is not the file format.</b> When a sketch becomes a feature of a document in Phase 5 it
/// will be written in MessagePack with everything else, canonically and deterministically, because
/// §5.8's first exit criterion is a bit-identical re-save. The same split already exists one layer
/// up: <c>omcad build</c> takes a JSON document spec and writes a MessagePack document, and nobody
/// confuses the two.
/// </para>
/// <para>
/// Everything is named, never positional. A constraint kind written as an ordinal, or an operand
/// list whose meaning came from its position in an array, would change meaning the moment a kind
/// was inserted — and a fixture corpus exists precisely to be read years later.
/// </para>
/// </remarks>
public static class SketchFormat
{
    /// <summary>The version this build writes.</summary>
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes a sketch.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <returns>The text.</returns>
    public static string Write(Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        JsonArray entities = [];

        foreach (SketchEntity entity in sketch.Entities.Ordered)
        {
            entities.Add(WriteEntity(entity));
        }

        JsonArray constraints = [];

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            constraints.Add(WriteConstraint(constraint));
        }

        JsonObject root = new()
        {
            ["version"] = Version,
            ["entities"] = entities,
            ["constraints"] = constraints,
        };

        return root.ToJsonString(Options) + "\n";
    }

    /// <summary>Reads a sketch back.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The sketch.</returns>
    /// <exception cref="SketchFormatException">The text is not a sketch this build can read.</exception>
    public static Sketch Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            JsonObject root = JsonNode.Parse(json) as JsonObject
                ?? throw new SketchFormatException("A sketch is a JSON object, and that is not.");

            int version = root["version"]?.GetValue<int>() ?? Version;

            if (version > Version)
            {
                throw new SketchFormatException(
                    $"This sketch was written at version {version} and this build reads {Version}.");
            }

            Sketch sketch = Sketch.Empty;

            foreach (JsonNode? node in root["entities"] as JsonArray ?? [])
            {
                sketch = sketch.With(ReadEntity(Object(node, "entity")));
            }

            foreach (JsonNode? node in root["constraints"] as JsonArray ?? [])
            {
                sketch = sketch.With(ReadConstraint(Object(node, "constraint")));
            }

            return sketch;
        }
        catch (Exception failure) when (failure
            is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new SketchFormatException(
                $"This is not a sketch this build can read: {failure.Message}", failure);
        }
    }

    private static JsonObject WriteEntity(SketchEntity entity)
    {
        JsonObject json = new()
        {
            ["id"] = entity.Id.ToStorageString(),
            ["kind"] = entity.Kind,
        };

        if (entity.IsConstruction)
        {
            json["construction"] = true;
        }

        switch (entity)
        {
            case SketchPoint point:
                json["at"] = Point(point.Position);
                break;

            case SketchLine line:
                json["from"] = Point(line.Start);
                json["to"] = Point(line.End);
                break;

            case SketchCircle circle:
                json["centre"] = Point(circle.Centre);
                json["radius"] = circle.Radius;
                break;

            case SketchArc arc:
                json["centre"] = Point(arc.Centre);
                json["radius"] = arc.Radius;
                json["from"] = arc.StartAngle;
                json["to"] = arc.EndAngle;
                break;

            case SketchEllipticalArc arc:
                json["centre"] = Point(arc.Centre);
                json["major"] = arc.MajorRadius;
                json["minor"] = arc.MinorRadius;
                json["rotation"] = arc.Rotation;
                json["from"] = arc.StartAngle;
                json["to"] = arc.EndAngle;
                break;

            case SketchEllipse ellipse:
                json["centre"] = Point(ellipse.Centre);
                json["major"] = ellipse.MajorRadius;
                json["minor"] = ellipse.MinorRadius;
                json["rotation"] = ellipse.Rotation;
                break;

            case SketchParabola parabola:
                json["vertex"] = Point(parabola.Vertex);
                json["focus"] = Point(parabola.Focus);
                json["from"] = parabola.StartParameter;
                json["to"] = parabola.EndParameter;
                break;

            case SketchHyperbola hyperbola:
                json["centre"] = Point(hyperbola.Centre);
                json["major"] = hyperbola.MajorRadius;
                json["minor"] = hyperbola.MinorRadius;
                json["rotation"] = hyperbola.Rotation;
                json["from"] = hyperbola.StartParameter;
                json["to"] = hyperbola.EndParameter;
                break;

            case SketchBSpline spline:
                json["degree"] = spline.Degree;
                json["poles"] = Points(spline.ControlPoints);
                json["weights"] = Numbers(spline.PoleWeights);
                json["knots"] = Numbers(spline.KnotValues);
                json["multiplicities"] = Counts(spline.KnotMultiplicities);

                if (spline.IsPeriodic)
                {
                    json["periodic"] = true;
                }

                break;

            default:
                throw new SketchFormatException(
                    $"There is no way to write a {entity.Kind}. A kind of geometry added without a "
                    + "case here would be dropped on save.");
        }

        return json;
    }

    private static SketchEntity ReadEntity(JsonObject json)
    {
        SketchEntityId id = SketchEntityId.Parse(Text(json, "id"));
        bool construction = json["construction"]?.GetValue<bool>() ?? false;

        return Text(json, "kind") switch
        {
            "point" => new SketchPoint(id, ReadPoint(json, "at"), construction),

            "line" => new SketchLine(
                id, ReadPoint(json, "from"), ReadPoint(json, "to"), construction),

            "circle" => new SketchCircle(
                id, ReadPoint(json, "centre"), Number(json, "radius"), construction),

            "arc" => new SketchArc(
                id,
                ReadPoint(json, "centre"),
                Number(json, "radius"),
                Number(json, "from"),
                Number(json, "to"),
                construction),

            "ellipse" => new SketchEllipse(
                id,
                ReadPoint(json, "centre"),
                Number(json, "major"),
                Number(json, "minor"),
                Number(json, "rotation"),
                construction),

            "elliptical arc" => new SketchEllipticalArc(
                id,
                ReadPoint(json, "centre"),
                Number(json, "major"),
                Number(json, "minor"),
                Number(json, "rotation"),
                Number(json, "from"),
                Number(json, "to"),
                construction),

            "parabola" => new SketchParabola(
                id,
                ReadPoint(json, "vertex"),
                ReadPoint(json, "focus"),
                Number(json, "from"),
                Number(json, "to"),
                construction),

            "hyperbola" => new SketchHyperbola(
                id,
                ReadPoint(json, "centre"),
                Number(json, "major"),
                Number(json, "minor"),
                Number(json, "rotation"),
                Number(json, "from"),
                Number(json, "to"),
                construction),

            "spline" => new SketchBSpline(
                id,
                (int)Number(json, "degree"),
                ReadPoints(json, "poles"),
                ReadNumbers(json, "weights"),
                ReadNumbers(json, "knots"),
                [.. ReadNumbers(json, "multiplicities").Select(m => (int)m)],
                json["periodic"]?.GetValue<bool>() ?? false,
                construction),

            var kind => throw new SketchFormatException(
                $"'{kind}' is not a kind of geometry this build knows."),
        };
    }

    private static JsonObject WriteConstraint(SketchConstraint constraint)
    {
        JsonArray operands = [];

        foreach (SketchPointRef operand in constraint.On)
        {
            JsonObject json = new() { ["entity"] = operand.Entity.ToStorageString() };

            if (operand.Point != EntityPoint.Self)
            {
                json["point"] = operand.Point.ToString();
            }

            operands.Add(json);
        }

        JsonObject constraintJson = new()
        {
            ["id"] = constraint.Id.ToStorageString(),
            ["kind"] = constraint.Kind.ToString(),
            ["on"] = operands,
        };

        if (constraint.Value is { } value)
        {
            constraintJson["value"] = value;
        }

        if (!constraint.IsDriving)
        {
            constraintJson["reference"] = true;
        }

        return constraintJson;
    }

    private static SketchConstraint ReadConstraint(JsonObject json)
    {
        if (!Enum.TryParse(Text(json, "kind"), out ConstraintKind kind))
        {
            throw new SketchFormatException(
                $"'{Text(json, "kind")}' is not a constraint this build knows.");
        }

        ImmutableArray<SketchPointRef>.Builder operands =
            ImmutableArray.CreateBuilder<SketchPointRef>();

        foreach (JsonNode? node in json["on"] as JsonArray ?? [])
        {
            JsonObject operand = Object(node, "operand");

            EntityPoint point = operand["point"] is { } which
                && Enum.TryParse(which.GetValue<string>(), out EntityPoint parsed)
                    ? parsed
                    : EntityPoint.Self;

            operands.Add(new SketchPointRef(
                SketchEntityId.Parse(Text(operand, "entity")), point));
        }

        return new SketchConstraint(
            SketchConstraintId.Parse(Text(json, "id")),
            kind,
            operands.ToImmutable(),
            json["value"]?.GetValue<double>(),
            !(json["reference"]?.GetValue<bool>() ?? false));
    }

    private static JsonArray Point(Vec2d point) => [point.X, point.Y];

    private static JsonArray Points(ImmutableArray<Vec2d> points)
    {
        JsonArray array = [];

        foreach (Vec2d point in points)
        {
            array.Add(Point(point));
        }

        return array;
    }

    private static JsonArray Numbers(ImmutableArray<double> values)
    {
        JsonArray array = [];

        foreach (double value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray Counts(ImmutableArray<int> values)
    {
        JsonArray array = [];

        foreach (int value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static Vec2d ReadPoint(JsonObject json, string name)
    {
        JsonArray pair = json[name] as JsonArray
            ?? throw new SketchFormatException($"This entity has no '{name}'.");

        return pair.Count == 2
            ? new Vec2d(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>())
            : throw new SketchFormatException(
                $"'{name}' has {pair.Count} numbers in it and a point has two.");
    }

    private static ImmutableArray<Vec2d> ReadPoints(JsonObject json, string name)
    {
        ImmutableArray<Vec2d>.Builder found = ImmutableArray.CreateBuilder<Vec2d>();

        foreach (JsonNode? node in json[name] as JsonArray ?? [])
        {
            JsonArray pair = node as JsonArray
                ?? throw new SketchFormatException($"'{name}' holds something that is not a point.");

            found.Add(new Vec2d(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
        }

        return found.ToImmutable();
    }

    private static ImmutableArray<double> ReadNumbers(JsonObject json, string name)
    {
        ImmutableArray<double>.Builder found = ImmutableArray.CreateBuilder<double>();

        foreach (JsonNode? node in json[name] as JsonArray ?? [])
        {
            found.Add(node!.GetValue<double>());
        }

        return found.ToImmutable();
    }

    private static JsonObject Object(JsonNode? node, string what)
        => node as JsonObject
            ?? throw new SketchFormatException($"An {what} is a JSON object, and that is not.");

    private static string Text(JsonObject json, string name)
        => json[name]?.GetValue<string>()
            ?? throw new SketchFormatException($"This has no '{name}'.");

    private static double Number(JsonObject json, string name)
        => json[name]?.GetValue<double>()
            ?? throw new SketchFormatException($"This has no '{name}'.");
}

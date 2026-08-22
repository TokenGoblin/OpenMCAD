using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OpenMCAD.Kernel.Fake.Geometry;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Fake;

/// <summary>
/// Serialises a fake shape to opaque bytes and back.
/// </summary>
/// <remarks>
/// <para>
/// Stands in for OCCT's BREP format. The bytes are opaque to everything above the kernel layer, so
/// the format only has to satisfy two things: round-trip fidelity, and byte-for-byte determinism.
/// </para>
/// <para>
/// Determinism matters more than it looks. From P3-T18 these blobs are the geometry cache inside a
/// saved document, and a format that serialised the same shape differently on two runs would make
/// every saved file differ from itself and defeat the double-rebuild diff in P1-T12.
/// </para>
/// <para>
/// Only the geometry is written, not the topology: the topology is rebuilt deterministically from
/// the geometry on read, which is both smaller and impossible to get out of step with itself.
/// </para>
/// </remarks>
internal static class FakeBRep
{
    private const uint Magic = 0x4B424D4F; // "OMBK"
    private const ushort Version = 1;

    private enum GeometryKind : ushort
    {
        Box = 1,
        Cylinder = 2,
        Sphere = 3,
        Cone = 4,
        Torus = 5,
        Profile = 6,
        Prism = 7,
        Composite = 8,
    }

    internal static ImmutableArray<byte> Write(FakeShape shape)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(shape.IsSolid);

        switch (shape.Geometry)
        {
            case BoxGeometry box:
                writer.Write((ushort)GeometryKind.Box);
                WriteVec3(writer, box.Size);
                WriteTransform(writer, box.Placement);
                break;

            case CylinderGeometry cylinder:
                writer.Write((ushort)GeometryKind.Cylinder);
                writer.Write(cylinder.Radius);
                writer.Write(cylinder.Height);
                WriteTransform(writer, cylinder.Placement);
                break;

            case SphereGeometry sphere:
                writer.Write((ushort)GeometryKind.Sphere);
                writer.Write(sphere.Radius);
                WriteTransform(writer, sphere.Placement);
                break;

            case ConeGeometry cone:
                writer.Write((ushort)GeometryKind.Cone);
                writer.Write(cone.BottomRadius);
                writer.Write(cone.TopRadius);
                writer.Write(cone.Height);
                WriteTransform(writer, cone.Placement);
                break;

            case TorusGeometry torus:
                writer.Write((ushort)GeometryKind.Torus);
                writer.Write(torus.MajorRadius);
                writer.Write(torus.MinorRadius);
                WriteTransform(writer, torus.Placement);
                break;

            case ProfileGeometry profile:
                writer.Write((ushort)GeometryKind.Profile);
                WritePolygon(writer, profile.Points);
                WriteTransform(writer, profile.Frame);
                break;

            case PrismGeometry prism:
                writer.Write((ushort)GeometryKind.Prism);
                WritePolygon(writer, prism.Points);
                WriteTransform(writer, prism.Frame);
                WriteVec3(writer, prism.Direction);
                writer.Write(prism.Distance);
                break;

            case CompositeGeometry composite:
                writer.Write((ushort)GeometryKind.Composite);
                writer.Write(composite.VolumeValue);
                writer.Write(composite.AreaValue);
                WriteVec3(writer, composite.BoundsValue.IsEmpty ? Vec3d.Zero : composite.BoundsValue.Min);
                WriteVec3(writer, composite.BoundsValue.IsEmpty ? Vec3d.Zero : composite.BoundsValue.Max);
                break;

            default:
                throw new NotSupportedException(
                    $"FakeBRep cannot serialise {shape.Geometry.GetType().Name}.");
        }

        writer.Flush();
        return [.. stream.ToArray()];
    }

    internal static void Read(
        ReadOnlySpan<byte> data,
        Func<ulong> nextTag,
        out FakeGeometry geometry,
        out TopologyBuilder topology,
        out bool isSolid)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("Not an OpenMCAD fake-kernel B-rep blob.");
        }

        using MemoryStream stream = new(data.ToArray(), writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        _ = reader.ReadUInt32();
        ushort version = reader.ReadUInt16();
        if (version != Version)
        {
            throw new InvalidDataException(
                $"Fake-kernel B-rep version {version} is not supported by this build (expected {Version}).");
        }

        isSolid = reader.ReadBoolean();
        GeometryKind kind = (GeometryKind)reader.ReadUInt16();

        topology = new TopologyBuilder(nextTag);

        switch (kind)
        {
            case GeometryKind.Box:
            {
                Vec3d size = ReadVec3(reader);
                Transform placement = ReadTransform(reader);
                geometry = new BoxGeometry(size, placement);
                topology.BuildBox(size, placement);
                break;
            }

            case GeometryKind.Cylinder:
            {
                double radius = reader.ReadDouble();
                double height = reader.ReadDouble();
                Transform placement = ReadTransform(reader);
                geometry = new CylinderGeometry(radius, height, placement);
                topology.BuildCylinder(radius, height, placement);
                break;
            }

            case GeometryKind.Sphere:
            {
                double radius = reader.ReadDouble();
                Transform placement = ReadTransform(reader);
                geometry = new SphereGeometry(radius, placement);
                topology.BuildSphere(radius, placement);
                break;
            }

            case GeometryKind.Cone:
            {
                double bottom = reader.ReadDouble();
                double top = reader.ReadDouble();
                double height = reader.ReadDouble();
                Transform placement = ReadTransform(reader);
                geometry = new ConeGeometry(bottom, top, height, placement);
                topology.BuildCone(bottom, top, height, placement);
                break;
            }

            case GeometryKind.Torus:
            {
                double major = reader.ReadDouble();
                double minor = reader.ReadDouble();
                Transform placement = ReadTransform(reader);
                geometry = new TorusGeometry(major, minor, placement);
                topology.BuildTorus(major, minor, placement);
                break;
            }

            case GeometryKind.Profile:
            {
                ImmutableArray<Vec2d> points = ReadPolygon(reader);
                Transform frame = ReadTransform(reader);
                geometry = new ProfileGeometry(points, frame);
                topology.BuildProfile(points, frame);
                break;
            }

            case GeometryKind.Prism:
            {
                ImmutableArray<Vec2d> points = ReadPolygon(reader);
                Transform frame = ReadTransform(reader);
                Vec3d direction = ReadVec3(reader);
                double distance = reader.ReadDouble();
                geometry = new PrismGeometry(points, frame, direction, distance);
                topology.BuildPrism(points, frame, direction, distance, out _, out _, out _, out _);
                break;
            }

            case GeometryKind.Composite:
            {
                double volume = reader.ReadDouble();
                double area = reader.ReadDouble();
                Vec3d min = ReadVec3(reader);
                Vec3d max = ReadVec3(reader);
                Bounds3d bounds = new(min, max);
                geometry = new CompositeGeometry(volume, area, bounds);
                topology.BuildBox(bounds.Size, Transform.FromTranslation(bounds.Min));
                break;
            }

            default:
                throw new InvalidDataException($"Unknown fake-kernel geometry kind {(int)kind}.");
        }
    }

    private static void WriteVec3(BinaryWriter writer, Vec3d value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vec3d ReadVec3(BinaryReader reader)
        => new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    private static void WriteTransform(BinaryWriter writer, Transform value)
    {
        writer.Write(value.Rotation.X);
        writer.Write(value.Rotation.Y);
        writer.Write(value.Rotation.Z);
        writer.Write(value.Rotation.W);
        WriteVec3(writer, value.Translation);
        writer.Write(value.Scale);
    }

    private static Transform ReadTransform(BinaryReader reader)
    {
        Quatd rotation = new(
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

        return new Transform(rotation, ReadVec3(reader), reader.ReadDouble());
    }

    private static void WritePolygon(BinaryWriter writer, ImmutableArray<Vec2d> points)
    {
        writer.Write(points.Length);
        foreach (Vec2d point in points)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
        }
    }

    private static ImmutableArray<Vec2d> ReadPolygon(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 1_000_000)
        {
            // Never trust a declared size from a file (PLAN.md R14). A corrupt length here would
            // otherwise become a multi-gigabyte allocation.
            throw new InvalidDataException($"Implausible polygon vertex count {count}.");
        }

        ImmutableArray<Vec2d>.Builder builder = ImmutableArray.CreateBuilder<Vec2d>(count);
        for (int i = 0; i < count; i++)
        {
            builder.Add(new Vec2d(reader.ReadDouble(), reader.ReadDouble()));
        }

        return builder.MoveToImmutable();
    }
}

/// <summary>
/// Writes a minimal, well-formed STEP file carrying product structure but no geometry.
/// </summary>
/// <remarks>
/// FakeKernel has no B-rep to export, and inventing one would be worse than useless. What this does
/// give is a real exercise of the export plumbing — stream handling, encoding, byte counts — with a
/// file a STEP reader will parse rather than reject. The caller is told plainly that the geometry
/// is absent, via a warning on the result.
/// </remarks>
internal static class FakeStepWriter
{
    internal static int Write(Stream destination, IReadOnlyList<FakeShape> shapes)
    {
        // ASCII, no byte-order mark: ISO 10303-21 is a plain-text format and a BOM breaks readers.
        using StreamWriter writer = new(destination, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            NewLine = "\n",
        };

        writer.WriteLine("ISO-10303-21;");
        writer.WriteLine("HEADER;");
        writer.WriteLine("FILE_DESCRIPTION(('OpenMCAD FakeKernel export - product structure only, no geometry'),'2;1');");

        // No timestamp: a file that differs between two identical exports would defeat the
        // determinism gate. ADR-0011 applies to everything the kernel emits, not just shapes.
        writer.WriteLine("FILE_NAME('openmcad-export.step','1970-01-01T00:00:00',('OpenMCAD'),(''),'OpenMCAD FakeKernel','OpenMCAD','');");
        writer.WriteLine("FILE_SCHEMA(('AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF'));");
        writer.WriteLine("ENDSEC;");
        writer.WriteLine("DATA;");

        int id = 1;
        for (int i = 0; i < shapes.Count; i++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"Body{i + 1}");
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"#{id++}=PRODUCT('{name}','{name}','',(#{id}));"));

            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"#{id++}=PRODUCT_CONTEXT('',#0,'mechanical');"));
        }

        writer.WriteLine("ENDSEC;");
        writer.WriteLine("END-ISO-10303-21;");
        writer.Flush();

        return (int)writer.BaseStream.Position;
    }
}

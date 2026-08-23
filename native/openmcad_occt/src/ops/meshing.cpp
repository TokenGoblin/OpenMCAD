/*
 * Tessellation (P1-T06).
 *
 * The viewport needs triangles; the kernel holds analytic surfaces. This is the only place the two
 * meet, and the mesh it produces is a derived cache, never a source of truth: nothing here feeds
 * back into geometry, and no query answers itself from a triangulation (see the bounding_box
 * comment in queries.cpp for what goes wrong when one does).
 *
 * Triangles are attributed back to the face they came from, because selection in the viewport is a
 * pick against a triangle that has to resolve to a nameable entity (ADR-0005).
 */

#include <algorithm>
#include <cmath>
#include <memory>

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRep_Tool.hxx>
#include <GCPnts_TangentialDeflection.hxx>
#include <Poly_PolygonOnTriangulation.hxx>
#include <TopExp.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <IMeshTools_Parameters.hxx>
#include <Poly_Triangulation.hxx>
#include <TopLoc_Location.hxx>
#include <TopoDS.hxx>
#include <gp_Dir.hxx>
#include <gp_Pnt.hxx>
#include <gp_Vec.hxx>

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ops.g.h"

namespace openmcad::ops {

namespace {

/*
 * The outward normal of a face at one of its triangulation nodes.
 *
 * Taken from the surface rather than from the facet, so a coarsely tessellated cylinder still
 * shades as a cylinder. Falls back to the facet normal where the surface has no well-defined
 * tangent plane -- at a cone apex, or on a degenerate patch -- which is rare but not an error.
 */
bool surfaceNormalAt(
    const BRepAdaptor_Surface& surface, double u, double v, gp_Vec& normal)
{
    gp_Pnt position;
    gp_Vec du;
    gp_Vec dv;
    surface.D1(u, v, position, du, dv);

    const gp_Vec candidate = du.Crossed(dv);
    if (candidate.SquareMagnitude() < 1.0e-24)
    {
        return false;
    }

    normal = candidate.Normalized();
    return true;
}


/*
 * Appends one edge's polyline to the mesh.
 *
 * Taken from the triangulation wherever possible, not from the analytic curve. The two agree only
 * to within the chordal deviation, and an edge drawn from the curve therefore floats above or sinks
 * below the tessellated surface it is supposed to bound -- which reads as z-fighting on a coarse
 * mesh and as a visibly detached outline on a curved one. Using the polygon OCCT already fitted to
 * the triangulation makes the edge lie exactly on the drawn surface by construction.
 *
 * The analytic fallback exists for edges no face carries a polygon for: a free edge in a wire body,
 * or a face the mesher declined. Those cannot z-fight with a surface that is not there.
 */
void appendEdge(
    MeshRecord& mesh, const TopoDS_Edge& edge, const ShapeAncestorMap& edgeToFaces,
    double angularDeviation)
{
    const size_t before = mesh.edgePositions.size();

    if (edgeToFaces.Contains(edge))
    {
        for (const TopoDS_Shape& shape : edgeToFaces.FindFromKey(edge))
        {
            const TopoDS_Face face = TopoDS::Face(shape);

            TopLoc_Location location;
            const Handle(Poly_Triangulation) triangulation =
                BRep_Tool::Triangulation(face, location);
            if (triangulation.IsNull())
            {
                continue;
            }

            const Handle(Poly_PolygonOnTriangulation) polygon =
                BRep_Tool::PolygonOnTriangulation(edge, triangulation, location);
            if (polygon.IsNull())
            {
                continue;
            }

            const gp_Trsf& placement = location.Transformation();
            for (int i = 1; i <= polygon->NbNodes(); ++i)
            {
                const gp_Pnt point =
                    triangulation->Node(polygon->Node(i)).Transformed(placement);

                mesh.edgePositions.push_back(point.X());
                mesh.edgePositions.push_back(point.Y());
                mesh.edgePositions.push_back(point.Z());
            }

            break;
        }
    }

    if (mesh.edgePositions.size() == before)
    {
        // Nothing tessellated carries this edge, so discretise the curve itself. Tangential
        // deflection rather than uniform sampling: it spends points where the curve bends and
        // none where it does not, so a straight edge costs two points and a tight arc stays smooth.
        BRepAdaptor_Curve curve(edge);
        GCPnts_TangentialDeflection points(
            curve, angularDeviation, curve.LastParameter() - curve.FirstParameter());

        for (int i = 1; i <= points.NbPoints(); ++i)
        {
            const gp_Pnt point = points.Value(i);
            mesh.edgePositions.push_back(point.X());
            mesh.edgePositions.push_back(point.Y());
            mesh.edgePositions.push_back(point.Z());
        }
    }

    // A polyline of one point draws nothing and would only make every consumer check for it.
    if (mesh.edgePositions.size() - before < 6)
    {
        mesh.edgePositions.resize(before);
        return;
    }

    mesh.edgeOffsets.push_back(static_cast<int32_t>(before / 3));
}

} /* namespace */

void triangulate(
    ShapeRef shape, double chordal_deviation, double angular_deviation, bool relative,
    bool compute_normals, MeshOut mesh)
{
    if (chordal_deviation <= 0.0)
    {
        throw invalid_input("The chordal deviation must be positive.");
    }

    if (angular_deviation <= 0.0)
    {
        throw invalid_input("The angular deviation must be positive.");
    }

    const TopoDS_Shape& solid = handles().resolve(shape);

    IMeshTools_Parameters parameters;
    parameters.Deflection = chordal_deviation;
    parameters.Angle = angular_deviation;
    parameters.Relative = relative;

    // ADR-0011 again, and for a sharper reason than the boolean. The parallel mesher assigns faces
    // to threads and the per-face vertex ordering follows completion order, so the same body
    // tessellated twice would produce the same triangles in a different order -- which is a
    // different hash, and the determinism gate compares hashes.
    parameters.InParallel = false;

    // Let the mesher relax rather than fail on a face it cannot hit the deflection on. A slightly
    // coarse triangle is a far better outcome for a viewport than no mesh at all.
    parameters.AllowQualityDecrease = true;

    // BRepMesh attaches the triangulation to the shape as a side effect. That is why write_brep
    // pins withTriangles to false: otherwise a body's serialised bytes would depend on whether
    // anything had rendered it.
    BRepMesh_IncrementalMesh mesher(solid, parameters);
    if (!mesher.IsDone())
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE, "The shape could not be tessellated.");
    }

    auto record = std::make_unique<MeshRecord>();

    // Canonical face order, so triangle attribution indexes the same faces in the same order on
    // every run -- and so the face tags handed back match the ones enumerate() gives.
    const std::vector<TopoDS_Shape> faces = enumerate_canonical(solid, TopAbs_FACE);

    for (int32_t faceIndex = 0; faceIndex < static_cast<int32_t>(faces.size()); ++faceIndex)
    {
        const TopoDS_Face face = TopoDS::Face(faces[static_cast<size_t>(faceIndex)]);
        record->faces.push_back(handles().store_entity(shape, face).tag);

        TopLoc_Location location;
        const Handle(Poly_Triangulation) triangulation = BRep_Tool::Triangulation(face, location);
        if (triangulation.IsNull())
        {
            // A face the mesher declined. It keeps its tag and its slot in the attribution table
            // so face indices stay aligned; it simply contributes no triangles.
            continue;
        }

        const gp_Trsf& placement = location.Transformation();

        // A reversed face has its triangles wound against the surface normal, so both the winding
        // and the normals have to be flipped for the outward side to face outward.
        const bool reversed = face.Orientation() == TopAbs_REVERSED;

        const int32_t base = record->vertex_count();
        const int nodes = triangulation->NbNodes();
        const bool haveUv = triangulation->HasUVNodes();

        std::unique_ptr<BRepAdaptor_Surface> surface;
        if (compute_normals && haveUv)
        {
            surface = std::make_unique<BRepAdaptor_Surface>(face);
        }

        for (int i = 1; i <= nodes; ++i)
        {
            const gp_Pnt point = triangulation->Node(i).Transformed(placement);
            record->positions.push_back(point.X());
            record->positions.push_back(point.Y());
            record->positions.push_back(point.Z());

            if (!compute_normals)
            {
                continue;
            }

            gp_Vec normal(0.0, 0.0, 1.0);
            bool known = false;

            if (surface)
            {
                const gp_Pnt2d uv = triangulation->UVNode(i);
                known = surfaceNormalAt(*surface, uv.X(), uv.Y(), normal);
            }

            if (!known)
            {
                // No tangent plane here -- a cone apex, or a face with no UV nodes. Zero is a
                // deliberate sentinel: a renderer can detect it and fall back to a facet normal,
                // which is better than shipping a confidently wrong direction.
                normal = gp_Vec(0.0, 0.0, 0.0);
            }
            else
            {
                normal.Transform(placement);
                if (reversed)
                {
                    normal.Reverse();
                }
            }

            record->normals.push_back(normal.X());
            record->normals.push_back(normal.Y());
            record->normals.push_back(normal.Z());
        }

        for (int i = 1; i <= triangulation->NbTriangles(); ++i)
        {
            int a = 0;
            int b = 0;
            int c = 0;
            triangulation->Triangle(i).Get(a, b, c);

            if (reversed)
            {
                std::swap(b, c);
            }

            // OCCT nodes are 1-based within the face; the mesh is 0-based and global.
            record->indices.push_back(base + a - 1);
            record->indices.push_back(base + b - 1);
            record->indices.push_back(base + c - 1);
            record->triangleFaces.push_back(faceIndex);
        }
    }

    /*
     * Edges, after the faces, because the polygons this reads are attached to the face
     * triangulations that the loop above just walked.
     *
     * Canonical order and the caller's own tags, so an edge picked in the viewport resolves to the
     * same entity `enumerate` would have named. An edge drawn under a tag the caller cannot
     * resolve is an edge that highlights and then selects nothing.
     */
    ShapeAncestorMap edgeToFaces;
    TopExp::MapShapesAndAncestors(solid, TopAbs_EDGE, TopAbs_FACE, edgeToFaces);

    for (const TopoDS_Shape& entity : enumerate_canonical(solid, TopAbs_EDGE))
    {
        const TopoDS_Edge edge = TopoDS::Edge(entity);

        // Degenerate edges are parameterisation artefacts with no length -- the poles of a sphere.
        // There is nothing to draw and nothing a user could point at.
        if (BRep_Tool::Degenerated(edge))
        {
            continue;
        }

        const size_t polylines = record->edgeOffsets.size();
        appendEdge(*record, edge, edgeToFaces, angular_deviation);

        if (record->edgeOffsets.size() != polylines)
        {
            record->edgeTags.push_back(handles().store_entity(shape, edge).tag);
        }
    }

    // The closing total, so polyline i spans [offsets[i], offsets[i+1]) with no special case for
    // the last one. Only when there is something to close.
    if (!record->edgeOffsets.empty())
    {
        record->edgeOffsets.push_back(static_cast<int32_t>(record->edgePositions.size() / 3));
    }

    mesh.set(handles().store(std::move(record)));
}

void mesh_counts(MeshRef mesh, OutBuffer<int32_t> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    const int32_t counts[3] = {
        record.vertex_count(), record.triangle_count(), record.face_count()};

    values.write(std::span<const int32_t>(counts, 3));
}

void mesh_positions(MeshRef mesh, OutBuffer<double> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(std::span<const double>(record.positions.data(), record.positions.size()));
}

void mesh_normals(MeshRef mesh, OutBuffer<double> values)
{
    // Legitimately empty when triangulate was called with compute_normals false. The two-call
    // protocol reports a required size of zero, which the managed side reads as "none", not as an
    // error -- asking for normals that were not requested is a reasonable thing for a caller to do.
    const MeshRecord& record = handles().resolve(mesh);
    values.write(std::span<const double>(record.normals.data(), record.normals.size()));
}

void mesh_indices(MeshRef mesh, OutBuffer<int32_t> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(std::span<const int32_t>(record.indices.data(), record.indices.size()));
}

void mesh_triangle_faces(MeshRef mesh, OutBuffer<int32_t> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(
        std::span<const int32_t>(record.triangleFaces.data(), record.triangleFaces.size()));
}

void mesh_faces(MeshRef mesh, OutBuffer<uint64_t> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(std::span<const uint64_t>(record.faces.data(), record.faces.size()));
}

void mesh_edge_offsets(MeshRef mesh, OutBuffer<int32_t> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(std::span<const int32_t>(record.edgeOffsets.data(), record.edgeOffsets.size()));
}

void mesh_edge_positions(MeshRef mesh, OutBuffer<double> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(
        std::span<const double>(record.edgePositions.data(), record.edgePositions.size()));
}

void mesh_edge_tags(MeshRef mesh, OutBuffer<uint64_t> values)
{
    const MeshRecord& record = handles().resolve(mesh);
    values.write(std::span<const uint64_t>(record.edgeTags.data(), record.edgeTags.size()));
}

} /* namespace openmcad::ops */

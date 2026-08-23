/*
 * Serialisation (P1-T06).
 *
 * Two formats with two different jobs. BREP is the kernel's own, used for the geometry cache and
 * for repro bundles: it round-trips exactly and nothing outside OpenMCAD has to read it. STEP is
 * the interchange format: it is lossy about history and nothing else will ever read our BREP.
 *
 * Both are pinned to explicit versions. An unpinned writer means an OCCT upgrade silently changes
 * the bytes a given model produces, which would fail the determinism gate for no modelling reason
 * and, worse, would invalidate every cached body at once.
 */

#include <cstdio>
#include <sstream>
#include <string>

#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <Interface_Static.hxx>
#include <STEPControl_StepModelType.hxx>
#include <STEPControl_Writer.hxx>
#include <TopTools_FormatVersion.hxx>
#include <TopoDS_Shape.hxx>

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ops.g.h"
#include "openmcad_roles.h"

namespace openmcad::ops {

namespace {

/*
 * The BREP format version this build writes.
 *
 * Pinned rather than defaulted. OCCT's default tracks the library, so upgrading it would change
 * the bytes for an unchanged model -- and the geometry cache is keyed by content hash. Raising
 * this is a deliberate act that invalidates caches; letting it drift is an accident that does the
 * same thing without anyone noticing.
 */
constexpr TopTools_FormatVersion kBrepVersion = TopTools_FormatVersion_VERSION_3;

} /* namespace */

void write_brep(ShapeRef shape, OutBuffer<uint8_t> data)
{
    const TopoDS_Shape& solid = handles().resolve(shape);

    std::ostringstream stream(std::ios::binary);

    // withTriangles false, withNormals false. A triangulation is a derived cache that
    // BRepMesh attaches to the shape in place, so including it would make a body's serialised
    // bytes depend on whether anything had rendered it first. Geometry only.
    BRepTools::Write(
        solid, stream, /* withTriangles */ false, /* withNormals */ false, kBrepVersion);

    if (!stream)
    {
        throw kernel_error(OPENMCAD_ERROR_KERNEL_FAILURE, "The shape could not be serialised.");
    }

    const std::string text = stream.str();
    data.write(std::span<const uint8_t>(
        reinterpret_cast<const uint8_t*>(text.data()), text.size()));
}

void read_brep(std::span<const uint8_t> data, ShapeOut result, HistoryOut history)
{
    if (data.empty())
    {
        throw invalid_input("There are no bytes to read.");
    }

    std::istringstream stream(
        std::string(reinterpret_cast<const char*>(data.data()), data.size()), std::ios::binary);

    TopoDS_Shape restored;
    BRep_Builder builder;

    try
    {
        BRepTools::Read(restored, stream, builder);
    }
    catch (const Standard_Failure& failure)
    {
        throw invalid_input(
            std::string("The data is not a readable BREP stream: ") + failure.what());
    }

    if (restored.IsNull())
    {
        throw invalid_input("The data did not contain a shape.");
    }

    const ShapeRef shape = handles().store(restored);
    auto record = std::make_unique<HistoryRecord>();

    // A read has no inputs, so every entity is created -- the same shape of map a primitive
    // produces. Imported is the honest role: these entities have no operation behind them, and a
    // naming reference to one cannot be re-derived, only re-matched.
    for (TopAbs_ShapeEnum kind : {TopAbs_FACE, TopAbs_EDGE, TopAbs_VERTEX})
    {
        sweep_created(*record, shape, kind, static_cast<int32_t>(Role::Imported));
    }

    result.set(shape);
    history.set(handles().store(std::move(record)));
}

void write_step(std::span<const uint64_t> shapes, const char* path, int32_t& bytes_written)
{
    if (shapes.empty())
    {
        throw invalid_input("There are no shapes to write.");
    }

    if (path == nullptr || *path == '\0')
    {
        throw invalid_input("A STEP file needs a path to write to.");
    }

    STEPControl_Writer writer;

    // AP242 is what the IDL promises, and it is not the OCCT default -- without this the file
    // comes out as AP214, which drops the geometric tolerance and datum entities AP242 carries.
    Interface_Static::SetCVal("write.step.schema", "AP242DIS");

    // Metres. The rest of the system is SI (ADR-0013) and the writer's default is millimetres, so
    // leaving this alone would scale every exported model by a thousand.
    Interface_Static::SetCVal("write.step.unit", "M");

    for (uint64_t tag : shapes)
    {
        const TopoDS_Shape& solid = handles().resolve(ShapeRef{tag});

        const IFSelect_ReturnStatus transferred = writer.Transfer(solid, STEPControl_AsIs);
        if (transferred != IFSelect_RetDone)
        {
            throw kernel_error(
                OPENMCAD_ERROR_KERNEL_FAILURE,
                "A shape could not be converted to STEP entities.");
        }
    }

    const IFSelect_ReturnStatus written = writer.Write(path);
    if (written != IFSelect_RetDone)
    {
        throw kernel_error(
            OPENMCAD_ERROR_KERNEL_FAILURE,
            std::string("The STEP file could not be written to ") + path + ".");
    }

    // The writer reports success but not size, and the caller wants to know the export produced
    // something. Measuring the file is the only honest way to answer.
    bytes_written = 0;
    if (std::FILE* handle = std::fopen(path, "rb"))
    {
        if (std::fseek(handle, 0, SEEK_END) == 0)
        {
            const long size = std::ftell(handle);
            if (size > 0)
            {
                bytes_written = static_cast<int32_t>(size);
            }
        }

        std::fclose(handle);
    }
}

} /* namespace openmcad::ops */

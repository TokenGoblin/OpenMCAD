/*
 * openmcad_roles.h - the C++ mirror of OpenMCAD.Kernel.OperationRole.
 *
 * These values cross the ABI and are persisted inside names, so they are append-only and must
 * never be renumbered. The managed enum in src/OpenMCAD.Kernel/OperationRole.cs is the source of
 * truth; this header mirrors it.
 *
 * OperationRoleMirrorTests in tests/arch parses both and fails the build if they disagree, which
 * is the only reason it is safe to keep two copies.
 */

#ifndef OPENMCAD_ROLES_H
#define OPENMCAD_ROLES_H

#include <cstdint>

namespace openmcad {

enum class Role : int32_t
{
    Unknown = 0,
    Retained = 1,
    Trimmed = 2,
    SideWall = 10,
    StartCap = 11,
    EndCap = 12,
    SideEdge = 13,
    StartProfileEdge = 14,
    EndProfileEdge = 15,
    Seam = 16,
    Apex = 17,
    BlendFace = 30,
    BlendEdge = 31,
    BlendCornerFace = 32,
    SetbackFace = 33,
    IntersectionEdge = 50,
    IntersectionVertex = 51,
    CoincidentFace = 52,
    SplitPositive = 70,
    SplitNegative = 71,
    OffsetFace = 90,
    ShellInnerFace = 91,
    ShellOpeningFace = 92,
    DraftFace = 93,
    PrimitiveFace = 110,
    PrimitiveEdge = 111,
    PrimitiveVertex = 112,
    PatternInstance = 130,
    MirrorImage = 131,
    Transformed = 132,
    Imported = 150,
};

} /* namespace openmcad */

#endif /* OPENMCAD_ROLES_H */

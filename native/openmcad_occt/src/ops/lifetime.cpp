/*
 * Lifetime operations (P1-T04).
 *
 * Thin by design: the handle table does the work, and these exist so the managed SafeHandle has
 * something to call.
 */

#include "openmcad_canonical.h"
#include "openmcad_handles.h"
#include "openmcad_ops.g.h"

namespace openmcad::ops {

void shape_release(ShapeRef shape)
{
    handles().release(shape);
}

void shape_add_ref(ShapeRef shape)
{
    handles().add_ref(shape);
}

void history_release(HistoryRef history)
{
    handles().release(history);
}

void mesh_release(MeshRef mesh)
{
    handles().release(mesh);
}

} /* namespace openmcad::ops */

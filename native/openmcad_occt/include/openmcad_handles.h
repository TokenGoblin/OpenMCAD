/*
 * openmcad_handles.h - the handle table (P1-T04).
 *
 * ADR-0003: "Handles are opaque uint64 tags in a shim-side handle table. No pointers cross the
 * boundary. This makes the boundary safe, debuggable, and -- importantly -- logically identical to
 * how Parasolid works, easing ADR-0002's swap path."
 *
 * Layout of a tag:
 *
 *     63                    40 39                                       0
 *     +-----------------------+-----------------------------------------+
 *     |  generation (24 bits) |            slot index (40 bits)         |
 *     +-----------------------+-----------------------------------------+
 *
 * The generation counter is the point. Slots are recycled, so without it a tag left over from a
 * released shape would silently resolve to whatever now occupies that slot -- returning the wrong
 * geometry rather than an error. With it, a stale tag fails a comparison. Zero is never a valid
 * tag, so a default-initialised handle is detectably empty.
 *
 * The kind is stored in the slot rather than in the tag, and checked on every resolve. Every
 * handle is the same 64-bit integer, so passing a mesh where a shape belongs is otherwise a
 * mistake nothing catches.
 *
 * Thread confinement: this table is touched only from the kernel thread (ADR-0004). It is not
 * synchronised, deliberately -- a lock here would imply the rule is negotiable.
 */

#ifndef OPENMCAD_HANDLES_H
#define OPENMCAD_HANDLES_H

#include <cstdint>
#include <deque>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

#include <NCollection_DataMap.hxx>
#include <NCollection_IndexedDataMap.hxx>
#include <NCollection_IndexedMap.hxx>
#include <NCollection_List.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <TopoDS_Shape.hxx>

#include "openmcad_types.h"

namespace openmcad {

/*
 * OCCT 8.0 deprecated the TopTools_* collection aliases in favour of the explicit templates.
 * Aliased once here so the spellings live in one place if they change again.
 *
 * ShapeList is what every OCCT algorithm returns from Generated and Modified, so it is spelled
 * often enough to be worth the alias even though it is only one template argument.
 */
using ShapeIndexedMap = NCollection_IndexedMap<TopoDS_Shape, TopTools_ShapeMapHasher>;
using ShapeList = NCollection_List<TopoDS_Shape>;
using ShapeAncestorMap =
    NCollection_IndexedDataMap<TopoDS_Shape, ShapeList, TopTools_ShapeMapHasher>;
using ShapeTagMap = NCollection_DataMap<TopoDS_Shape, uint64_t, TopTools_ShapeMapHasher>;

/* --- payloads ------------------------------------------------------------------------------- */

/*
 * The provenance accumulated by one operation, in the form the history_* entry points serve it.
 *
 * Keyed by tag rather than by TopoDS_Shape, because the managed side only ever knows tags, and
 * translating on every query would mean keeping the input shapes alive for the life of the map.
 */
struct HistoryRecord
{
    std::map<uint64_t, std::set<uint64_t>> generated;   /* input tag -> outputs it created     */
    std::map<uint64_t, std::set<uint64_t>> modified;    /* input tag -> altered successors     */
    std::set<uint64_t> deleted;                         /* inputs with no successor            */
    std::set<uint64_t> created;                         /* outputs made from nothing           */
    std::map<uint64_t, int32_t> roles;                  /* output tag -> OperationRole         */
    std::map<uint64_t, uint64_t> sources;               /* output tag -> the input it came from*/

    /*
     * Records that an input caused an output to exist.
     *
     * Deletion and generation are orthogonal: a filleted edge is consumed AND is the reason the
     * blend face exists. The managed HistoryMapBuilder permits that pair, and so does this.
     */
    void add_generated(uint64_t input, uint64_t output, int32_t role);

    /* Records that an output is the altered successor of an input. Excludes deletion. */
    void add_modified(uint64_t input, uint64_t output, int32_t role);

    /* Records that an input has no successor. */
    void add_deleted(uint64_t input);

    /* Records an output created from nothing. */
    void add_created(uint64_t output, int32_t role);

    /*
     * Assigns an output's role, keeping the first one assigned.
     *
     * Operations record their most specific relationship first -- an extrusion names its caps
     * before it maps faces generally -- so first-wins keeps the role that carries information.
     */
    void assign_role(uint64_t output, int32_t role);

    /* Sorted, because determinism starts at the boundary (ADR-0011). */
    std::vector<uint64_t> generated_of(uint64_t input) const;
    std::vector<uint64_t> modified_of(uint64_t input) const;
    std::vector<uint64_t> new_entities() const;
    std::vector<uint64_t> outputs() const;
    std::vector<uint64_t> inputs() const;
};

/* A tessellation, in the form the mesh_* entry points serve it. */
struct MeshRecord
{
    std::vector<double> positions;     /* xyz triples, metres              */
    std::vector<double> normals;       /* xyz triples, may be empty        */
    std::vector<int32_t> indices;      /* three per triangle               */
    std::vector<int32_t> triangleFaces;/* one face index per triangle      */
    std::vector<uint64_t> faces;       /* face tags the indices refer to   */

    /*
     * Edge polylines, concatenated. Polyline i spans points [edgeOffsets[i], edgeOffsets[i+1]),
     * so edgeOffsets has one more entry than there are polylines. Empty means no edges at all,
     * not one empty polyline.
     *
     * Kept apart from the face vertices rather than indexed into them. An edge point and a face
     * vertex coincide in space but not in the buffer -- the face array is grouped by face and an
     * edge is shared between two of them -- and sharing would mean an index remap for no saving
     * worth the bug.
     */
    std::vector<double> edgePositions;
    std::vector<int32_t> edgeOffsets;
    std::vector<uint64_t> edgeTags;

    int32_t vertex_count() const { return static_cast<int32_t>(positions.size() / 3); }
    int32_t triangle_count() const { return static_cast<int32_t>(indices.size() / 3); }
    int32_t face_count() const { return static_cast<int32_t>(faces.size()); }
    int32_t edge_count() const { return static_cast<int32_t>(edgeTags.size()); }
};

/* --- the table ------------------------------------------------------------------------------ */

enum class HandleKind : uint8_t
{
    Free = 0,
    Shape = 1,
    Entity = 2,
    History = 3,
    Mesh = 4,
};

const char* to_string(HandleKind kind) noexcept;

class HandleTable
{
public:
    static constexpr int IndexBits = 40;
    static constexpr uint64_t IndexMask = (uint64_t{1} << IndexBits) - 1;
    static constexpr uint64_t MaxIndex = IndexMask;
    static constexpr uint32_t GenerationMask = 0xFFFFFF; /* 24 bits */

    /* --- shapes ------------------------------------------------------------------------------ */

    /* Takes ownership of a shape and returns a tag with one reference. */
    ShapeRef store(const TopoDS_Shape& shape);

    /* Resolves a shape tag, or throws invalid_handle. */
    const TopoDS_Shape& resolve(ShapeRef ref) const;

    /* Takes an additional reference. */
    void add_ref(ShapeRef ref);

    /* Drops one reference. The shape and all its sub-entities go when the last one does. */
    void release(ShapeRef ref);

    /* --- sub-entities -------------------------------------------------------------------------
     *
     * Owned by their shape and released with it, so they are not reference counted individually.
     * A body with ten thousand faces must not need ten thousand refcounts, and their lifetime is
     * genuinely subordinate: a face cannot outlive the solid it bounds.
     */

    /* Returns the tag for a sub-entity, reusing it if this entity was already tagged. */
    EntityRef store_entity(ShapeRef owner, const TopoDS_Shape& entity);

    /* Resolves an entity tag, or throws invalid_handle. */
    const TopoDS_Shape& resolve(EntityRef ref) const;

    /* Returns the shape that owns an entity. */
    ShapeRef owner_of(EntityRef ref) const;

    /* --- history and meshes ------------------------------------------------------------------- */

    HistoryRef store(std::unique_ptr<HistoryRecord> record);
    const HistoryRecord& resolve(HistoryRef ref) const;
    void release(HistoryRef ref);

    MeshRef store(std::unique_ptr<MeshRecord> record);
    const MeshRecord& resolve(MeshRef ref) const;
    void release(MeshRef ref);

    /* --- diagnostics --------------------------------------------------------------------------- */

    /* How many slots are occupied. Zero after a clean shutdown; used by the leak tests. */
    size_t live_count() const noexcept { return live_; }

    /* How many slots have ever been allocated, including recycled ones. */
    size_t capacity() const noexcept { return slots_.size(); }

    /* Releases everything. Called at shutdown; not part of normal operation. */
    void clear() noexcept;

private:
    struct Slot
    {
        HandleKind kind = HandleKind::Free;
        uint32_t generation = 0;
        int32_t references = 0;

        TopoDS_Shape shape;                        /* Shape and Entity      */
        uint64_t owner = 0;                        /* Entity only           */
        std::unique_ptr<HistoryRecord> history;    /* History only          */
        std::unique_ptr<MeshRecord> mesh;          /* Mesh only             */

        /* Entity tags issued for this shape, so releasing a shape frees them too. */
        std::vector<uint64_t> entities;
    };

    uint64_t allocate(HandleKind kind);
    Slot& check(uint64_t tag, HandleKind expected);
    const Slot& check(uint64_t tag, HandleKind expected) const;
    void free_slot(uint64_t index) noexcept;

    static uint64_t make_tag(uint64_t index, uint32_t generation) noexcept
    {
        return index | (static_cast<uint64_t>(generation & GenerationMask) << IndexBits);
    }

    static uint64_t index_of(uint64_t tag) noexcept { return tag & IndexMask; }
    static uint32_t generation_of(uint64_t tag) noexcept
    {
        return static_cast<uint32_t>(tag >> IndexBits) & GenerationMask;
    }

    /*
     * A deque, not a vector, and this is a correctness requirement rather than a preference.
     *
     * resolve() hands back a `const TopoDS_Shape&` that points into a slot. Operations hold that
     * reference across the store() call that saves their result -- a fillet resolves its body,
     * builds, stores the new shape, and then walks the original to map history. A vector
     * reallocates on growth and every outstanding reference becomes dangling, which showed up as
     * an access violation inside the fillet rather than anywhere near the cause.
     *
     * std::deque never invalidates references to existing elements when it grows at the end, and
     * free_slot only marks a slot rather than erasing it, so a reference stays valid for as long
     * as its handle does.
     */
    std::deque<Slot> slots_;
    std::vector<uint64_t> free_;
    size_t live_ = 0;

    /*
     * Sub-entity tags already issued, keyed by owner and by the entity itself, so the same face
     * asked for twice gets the same tag. Without this, a naming layer comparing two references to
     * one face would see different tags and conclude they were different faces.
     *
     * Keyed with OCCT's own shape hasher rather than by the raw TShape pointer, and the difference
     * is not academic. OCCT shares one TShape between an entity and a relocated copy of it,
     * distinguishing the two only by their TopLoc_Location -- so an extruded square's top face,
     * its four top edges and its four top vertices all carry the same TShapes as the bottom ones.
     * Keying on the pointer collapsed those nine entities onto their originals, and they silently
     * vanished from the history: no role, no name, unreachable.
     *
     * TopTools_ShapeMapHasher compares by TShape and location while ignoring orientation, which is
     * exactly the identity a name needs -- a face and its reversed self are one face, a face and
     * its translated copy are two.
     */
    std::map<uint64_t, ShapeTagMap> entityTags_;
};

/* The process-wide table. One per process because the kernel is one actor (ADR-0004). */
HandleTable& handles();

} /* namespace openmcad */

#endif /* OPENMCAD_HANDLES_H */

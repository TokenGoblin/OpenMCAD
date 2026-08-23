/*
 * openmcad_handles.cpp - the handle table (P1-T04).
 */

#include "openmcad_handles.h"

#include <algorithm>
#include <TopoDS_TShape.hxx>

namespace openmcad {

/* --- HistoryRecord ---------------------------------------------------------------------------- */

/*
 * First writer wins, and the operations are written so that the first writer is the most specific.
 *
 * This used to be a plain assignment while `sources` used emplace, and the inconsistency was a
 * bug: an extrusion recorded its end caps as StartCap and EndCap, and then the general
 * face-mapping pass overwrote both with Transformed. The caps stopped being nameable and nothing
 * failed loudly, because a wrong role is still a role.
 *
 * The managed HistoryMapBuilder throws on a conflicting reassignment rather than choosing. It can
 * afford to: by the time it runs, the shim has already settled the question. Here both writers are
 * legitimate views of the same entity, and the specific one is the useful one.
 */
void HistoryRecord::assign_role(uint64_t output, int32_t role)
{
    roles.emplace(output, role);
}

void HistoryRecord::add_generated(uint64_t input, uint64_t output, int32_t role)
{
    generated[input].insert(output);
    assign_role(output, role);

    // First writer wins, matching the managed HistoryMapBuilder. An entity generated from several
    // inputs -- a blend face between two faces -- keeps the first as its nominal source, and
    // AddNewBetween on the managed side deliberately records none at all.
    sources.emplace(output, input);
}

void HistoryRecord::add_modified(uint64_t input, uint64_t output, int32_t role)
{
    modified[input].insert(output);
    assign_role(output, role);
    sources.emplace(output, input);
}

void HistoryRecord::add_deleted(uint64_t input)
{
    deleted.insert(input);
}

void HistoryRecord::add_created(uint64_t output, int32_t role)
{
    created.insert(output);
    assign_role(output, role);
}

namespace {

std::vector<uint64_t> sorted(const std::set<uint64_t>& values)
{
    // std::set is already ordered; the copy exists to hand back a contiguous buffer the two-call
    // pattern can memcpy.
    return std::vector<uint64_t>(values.begin(), values.end());
}

} /* namespace */

std::vector<uint64_t> HistoryRecord::generated_of(uint64_t input) const
{
    auto it = generated.find(input);
    return it == generated.end() ? std::vector<uint64_t>{} : sorted(it->second);
}

std::vector<uint64_t> HistoryRecord::modified_of(uint64_t input) const
{
    auto it = modified.find(input);
    return it == modified.end() ? std::vector<uint64_t>{} : sorted(it->second);
}

std::vector<uint64_t> HistoryRecord::new_entities() const
{
    return sorted(created);
}

std::vector<uint64_t> HistoryRecord::outputs() const
{
    std::vector<uint64_t> result;
    result.reserve(roles.size());
    for (const auto& entry : roles)
    {
        result.push_back(entry.first);
    }

    // std::map iterates in key order, so this is already sorted.
    return result;
}

std::vector<uint64_t> HistoryRecord::inputs() const
{
    std::set<uint64_t> all;
    for (const auto& entry : generated) { all.insert(entry.first); }
    for (const auto& entry : modified)  { all.insert(entry.first); }
    for (uint64_t tag : deleted)        { all.insert(tag); }
    return sorted(all);
}

/* --- HandleTable ------------------------------------------------------------------------------ */

const char* to_string(HandleKind kind) noexcept
{
    switch (kind)
    {
        case HandleKind::Shape:   return "shape";
        case HandleKind::Entity:  return "entity";
        case HandleKind::History: return "history";
        case HandleKind::Mesh:    return "mesh";
        default:                  return "free";
    }
}

uint64_t HandleTable::allocate(HandleKind kind)
{
    uint64_t index;

    if (!free_.empty())
    {
        index = free_.back();
        free_.pop_back();
    }
    else
    {
        if (slots_.size() >= MaxIndex)
        {
            throw kernel_error(
                OPENMCAD_ERROR_OUT_OF_MEMORY,
                "The handle table is exhausted: more than a trillion live handles.");
        }

        index = slots_.size();
        slots_.emplace_back();
    }

    Slot& slot = slots_[static_cast<size_t>(index)];
    slot.kind = kind;
    slot.references = 1;
    slot.owner = 0;
    slot.entities.clear();
    ++live_;

    // Index 0 with generation 0 would produce tag 0, which is reserved for "no handle". Skip
    // generation 0 on slot 0 so a valid handle is never mistaken for an empty one.
    if (index == 0 && slot.generation == 0)
    {
        slot.generation = 1;
    }

    return make_tag(index, slot.generation);
}

void HandleTable::free_slot(uint64_t index) noexcept
{
    Slot& slot = slots_[static_cast<size_t>(index)];

    slot.kind = HandleKind::Free;
    slot.references = 0;
    slot.shape.Nullify();
    slot.owner = 0;
    slot.history.reset();
    slot.mesh.reset();
    slot.entities.clear();

    // Bump the generation so every tag naming this slot is now stale. Wrapping is harmless: it
    // would take sixteen million reuses of one slot to alias, and the tag would have to have been
    // held across all of them.
    slot.generation = (slot.generation + 1) & GenerationMask;
    if (index == 0 && slot.generation == 0)
    {
        slot.generation = 1;
    }

    free_.push_back(index);
    --live_;
}

HandleTable::Slot& HandleTable::check(uint64_t tag, HandleKind expected)
{
    return const_cast<Slot&>(static_cast<const HandleTable*>(this)->check(tag, expected));
}

const HandleTable::Slot& HandleTable::check(uint64_t tag, HandleKind expected) const
{
    if (tag == 0)
    {
        throw invalid_handle(tag);
    }

    const uint64_t index = index_of(tag);
    if (index >= slots_.size())
    {
        throw invalid_handle(tag);
    }

    const Slot& slot = slots_[static_cast<size_t>(index)];

    // The generation check is what turns a use-after-release into an error instead of the wrong
    // answer. It must come before the kind check, because a recycled slot may legitimately hold a
    // different kind now.
    if (slot.generation != generation_of(tag) || slot.kind == HandleKind::Free)
    {
        throw invalid_handle(tag);
    }

    if (slot.kind != expected)
    {
        throw kernel_error(
            OPENMCAD_ERROR_INVALID_HANDLE,
            std::string("Handle ") + std::to_string(tag) + " is a " + to_string(slot.kind)
                + " but was used as a " + to_string(expected) + ".");
    }

    return slot;
}

/* --- shapes ------------------------------------------------------------------------------------ */

ShapeRef HandleTable::store(const TopoDS_Shape& shape)
{
    if (shape.IsNull())
    {
        throw invalid_input("Cannot store a null shape in the handle table.");
    }

    const uint64_t tag = allocate(HandleKind::Shape);
    slots_[static_cast<size_t>(index_of(tag))].shape = shape;
    return ShapeRef{tag};
}

const TopoDS_Shape& HandleTable::resolve(ShapeRef ref) const
{
    return check(ref.tag, HandleKind::Shape).shape;
}

void HandleTable::add_ref(ShapeRef ref)
{
    ++check(ref.tag, HandleKind::Shape).references;
}

void HandleTable::release(ShapeRef ref)
{
    Slot& slot = check(ref.tag, HandleKind::Shape);

    if (--slot.references > 0)
    {
        return;
    }

    // Sub-entity tags die with their shape. Copy the list first: free_slot clears it, and the
    // entity slots must be released before the owner's slot is recycled.
    const std::vector<uint64_t> entities = slot.entities;
    const uint64_t index = index_of(ref.tag);

    for (uint64_t entityTag : entities)
    {
        const uint64_t entityIndex = index_of(entityTag);
        if (entityIndex < slots_.size()
            && slots_[static_cast<size_t>(entityIndex)].kind == HandleKind::Entity
            && slots_[static_cast<size_t>(entityIndex)].generation == generation_of(entityTag))
        {
            free_slot(entityIndex);
        }
    }

    entityTags_.erase(ref.tag);
    free_slot(index);
}

/* --- sub-entities -------------------------------------------------------------------------------- */

EntityRef HandleTable::store_entity(ShapeRef owner, const TopoDS_Shape& entity)
{
    if (entity.IsNull())
    {
        throw invalid_input("Cannot store a null sub-entity in the handle table.");
    }

    // Confirms the owner exists and is a shape before anything else happens.
    check(owner.tag, HandleKind::Shape);

    // Identity is the shape itself under OCCT's hasher: same TShape, same location, orientation
    // ignored. Two explorations of the same face therefore yield the same tag, which the naming
    // layer depends on -- while a translated copy, which shares its TShape, correctly gets its own.
    ShapeTagMap& byIdentity = entityTags_[owner.tag];
    if (const uint64_t* existing = byIdentity.Seek(entity))
    {
        return EntityRef{*existing};
    }

    const uint64_t tag = allocate(HandleKind::Entity);
    Slot& slot = slots_[static_cast<size_t>(index_of(tag))];
    slot.shape = entity;
    slot.owner = owner.tag;

    byIdentity.Bind(entity, tag);
    slots_[static_cast<size_t>(index_of(owner.tag))].entities.push_back(tag);

    return EntityRef{tag};
}

const TopoDS_Shape& HandleTable::resolve(EntityRef ref) const
{
    return check(ref.tag, HandleKind::Entity).shape;
}

ShapeRef HandleTable::owner_of(EntityRef ref) const
{
    return ShapeRef{check(ref.tag, HandleKind::Entity).owner};
}

/* --- history and meshes ----------------------------------------------------------------------------- */

HistoryRef HandleTable::store(std::unique_ptr<HistoryRecord> record)
{
    if (!record)
    {
        throw invalid_input("Cannot store a null history record.");
    }

    const uint64_t tag = allocate(HandleKind::History);
    slots_[static_cast<size_t>(index_of(tag))].history = std::move(record);
    return HistoryRef{tag};
}

const HistoryRecord& HandleTable::resolve(HistoryRef ref) const
{
    return *check(ref.tag, HandleKind::History).history;
}

void HandleTable::release(HistoryRef ref)
{
    check(ref.tag, HandleKind::History);
    free_slot(index_of(ref.tag));
}

MeshRef HandleTable::store(std::unique_ptr<MeshRecord> record)
{
    if (!record)
    {
        throw invalid_input("Cannot store a null mesh record.");
    }

    const uint64_t tag = allocate(HandleKind::Mesh);
    slots_[static_cast<size_t>(index_of(tag))].mesh = std::move(record);
    return MeshRef{tag};
}

const MeshRecord& HandleTable::resolve(MeshRef ref) const
{
    return *check(ref.tag, HandleKind::Mesh).mesh;
}

void HandleTable::release(MeshRef ref)
{
    check(ref.tag, HandleKind::Mesh);
    free_slot(index_of(ref.tag));
}

/* --- lifetime --------------------------------------------------------------------------------------- */

void HandleTable::clear() noexcept
{
    slots_.clear();
    free_.clear();
    entityTags_.clear();
    live_ = 0;
}

HandleTable& handles()
{
    // Function-local static: constructed on first use, which keeps it out of static-initialisation
    // order problems with OCCT's own globals.
    static HandleTable table;
    return table;
}

} /* namespace openmcad */

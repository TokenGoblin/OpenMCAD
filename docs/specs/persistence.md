# Spec: persistence

**Status:** built. Schema 1 is the only version there has ever been, so the migration chain is
exercised by migrations declared in tests rather than by real ones, and the format corpus holds one
fixture. Both of those are correct for a project that has released nothing — and both change the
day a second version exists.

**Tasks:** P3-T18, P3-T19, P3-T20.

This expands PLAN.md §5.8 and records ADR-0010. The document model itself is `document-model.md`;
this is about getting one onto disk and back, forever.

§5.8 calls its versioning rules **non-negotiable from Phase 3**. Section 9 is the short version of
what that costs and who pays it.

---

## 1. What is here

`OpenMCAD.Core/Serialization`, seven files.

| Piece | Where |
|---|---|
| The container — read, write, manifest | `DocumentPackage.cs` |
| The document graph codec, `SchemaVersion` | `DocumentCodec.cs` |
| A hand-written MessagePack encoder and decoder | `MessagePackWriter.cs`, `MessagePackReader.cs` |
| The untyped tree a migration works on | `MessagePackValue.cs` |
| The migration chain and its registry | `SchemaMigration.cs` |
| Unknown-field capture | `UnknownField.cs` |
| Format fixtures and the CI gate | `tests/fixtures/format/`, `tests/unit/OpenMCAD.Core.Tests/FormatFixtureTests.cs` |

---

## 2. The container (ADR-0010, P3-T18)

`.ompart` / `.omasm` / `.omdrw` are Zip/OPC containers. `DocumentKind` is `Part | Assembly |
Drawing`.

```
/manifest.json              format version, schema version, app, kind, GUID, created/modified
/document.msgpack           the versioned document graph
/geometry/<featureId>.brep  cached kernel B-rep blobs      (regenerable)
/tessellation/<bodyId>.mesh cached display meshes          (regenerable)
/thumbnail.png              for Explorer/PDM/open dialog
/preview/<config>.png       per-configuration previews
/refs/external.json         external references
/custom/                    user and plugin property storage
```

**Two version numbers, and they are not the same thing.** `DocumentManifest.FormatVersion` is the
container layout — which parts exist and where. `SchemaVersion` is the document graph inside
`document.msgpack`. A part can be added to the container without the graph changing, and the graph
can change without the container moving, so conflating them would force a migration for a change
that needs none.

**Caches are never the source of truth.** A corrupt or missing `geometry/` or `tessellation/` means
*rebuild*, not data loss. `--no-cache` is the same reader with the caches ignored, so the two paths
can be compared against each other rather than against a second implementation.

**Container parts this build has no name for are kept and written back** (`PackageContents.Unrecognised`).
That is the whole of what forward compatibility means for something nobody can interpret.

**A target names a document, and the store decides what that means** (`IDocumentStore`, P5-T12).
Nothing above the store interprets the string: `ComponentDefinition.Source` and
`/refs/external.json` both carry one, and a folder, a vault or a fixture answers it differently.
`FileDocumentStore` reads it as a path relative to a root and refuses any target that escapes that
root — a document is data, and one that could name a path outside the folder it lives in would make
opening an untrusted file a way to read the machine.

**One part is not the caller's business.** Everything in `PackageContents` is opaque and comes from
whoever is saving — except `/refs/external.json`, which `Save` writes from the document and `Open`
folds back into it (P5-T12). The asymmetry is deliberate: the stamps in that part are the only
record of what each dependency was when it was last read, so a save command that forgot to compose
it would lose them silently and report every dependency as current from then on. What the field
carries after a read is what was on disk; the document is what is true.

---

## 3. Bytes are a function of the document (P3-T18)

Phase 3's first exit criterion. Everything below exists to make it true:

- **The encoder is canonical**, and every collection without an order of its own is **written
  sorted**.
- **Zip entries carry a fixed timestamp.** When a file was written is the manifest's business.
- **The manifest takes its timestamps as inputs rather than from the clock**, or no save could ever
  match another.
- **`WriteIndented` takes its newline from `Environment.NewLine`**, so the same document saved on
  Windows and on Linux differed in the manifest — exactly what the fixed timestamps and the
  canonical encoder exist to prevent, and invisible to a Windows-only CI. Fixed.

**What is deliberately not written:** a `KernelShape`, which is a handle into a kernel that is not
running any more; and the rebuild report, which §5.8 makes regenerable.

**Why the encoder is hand-written.** `Directory.Packages.props` requires an ADR for any new
dependency, and the one thing a MessagePack library would have given — attribute-driven
serialisation — is unusable, because P3-T20 needs explicit read and write code anyway.

### Two determinism tests that proved nothing

Both found by sabotage, and both worth remembering because the same mistake is easy to repeat:

1. **An `ImmutableDictionary` enumerates by content, not insertion order**, so building the document
   "in a different order" and comparing proved nothing at all. The written order is now read back
   out of the bytes and asserted directly.
2. **A Zip stores DOS time to a two-second resolution**, so two saves within one test land in the
   same tick whether or not the timestamp is fixed. Each entry's stamp is now checked.

---

## 4. Reading is not the reverse of writing (P3-T18)

**A MessagePack map has no defined order.** The reader originally applied each field to the document
as it arrived, so a legal file that put its bodies before its features — or its rollback bar before
either — threw on open. **Every field is now collected before anything is assembled.**

Four more from the same review, none of which the tests had an opinion about:

- **`Read` promised `DocumentFormatException` and let `FormatException` and `ArgumentException` out
  of the same call**, so a caller had to catch three types to catch one contract.
- **The datums were cleared unconditionally**, so a file that simply had nothing to say about
  reference geometry opened with no origin and no planes. Only a file that *states* what its
  geometry is — including that it has none — replaces them now.
- **`Skip` threw on the extension family**, and MessagePack's standard timestamp is an extension
  type, so a field another implementation could reasonably write failed the whole open.
- **`Save` persisted whatever versions the caller's manifest carried**, so the natural re-save
  recorded the old file's schema beside a payload written at the current one.

`DocumentPackage.Open` also promised `DocumentFormatException` and let `InvalidDataException` out
when the file was not a Zip at all — which is the commonest way to arrive with something wrong.
Found by P3-T22's tests.

---

## 5. Migration (P3-T19)

**A migration cannot be handed a `Document`.** It reads a document this build's codec by definition
cannot read. Asking it to patch raw bytes would make every migration a hand-written parser, so it
gets a `MessagePackValue` tree instead: the shape is visible, nothing is validated, and re-encoding
produces bytes the current reader takes.

Rules, each of which is a decision rather than an implementation detail:

- **Maps keep their order.** A tree that reordered fields would break §3's bit-identical re-save the
  moment a migration touched a file. That is why `With` on an existing key replaces it *in place*,
  and why `Renamed` exists at all rather than leaving every migration to remove-and-add and move the
  field to the end.
- **Extension values are kept verbatim.** Re-encoding a value nothing here understands is how a
  migration would silently corrupt one.
- **Steps are single-version only.** A migration that jumped two could not be composed with the ones
  around it, and the first version inserted between them would invalidate every such shortcut.
- **A gap in the chain is refused, not skipped.** Skipping produces a file that opens, looks right,
  and is wrong in whatever way the missing step existed to fix.
- **Two migrations claiming one version are refused**, not resolved by declaration order.
- **The chain stamps the version each step reached**, so a migration that forgot does not surface as
  a confusing complaint about the file.
- **The reader checks the version before parsing** and only pays for the tree when a document is
  actually old.
- **A file with no `schema` field is read as current.** A guess either way, and this is the one that
  changes nothing.

`SchemaMigration.Known` is **empty**, because schema 1 is the only version there has ever been. The
chain is exercised by migrations declared in the tests; ten sabotages each fail the right one.

---

## 6. The format corpus and the CI gate (P3-T19)

§5.8: *the corpus keeps at least one fixture saved by every released version, CI opens all of them
on every build, and breaking an old file fails the build.*

`tests/fixtures/format/` holds **real packages, never regenerated**, each with a JSON description
beside it covering **every field the schema carries** — feature suppression and a derived
parameter's expression included, since an expectation checking only names would let a migration drop
the rest unnoticed.

**Values are recorded round-trippable, not through `Quantity.ToString`**, which rounds thirty degrees
to 0.523599 and would let a migration move a dimension by a part in ten thousand and still pass.

**Raising `SchemaVersion` without adding a fixture fails**, and the failing run writes the candidate
package and says where to put it — so the gate tells you how to satisfy it rather than only that you
have not.

**The corpus is exempt from git-lfs** that the root `.gitattributes` would otherwise apply. These are
two kilobytes each and the build fails without them; behind lfs, a clone without the filter gets
pointer files and the gate then reports that OpenMCAD cannot open its own historical documents —
a true statement about the working tree and a completely misleading one about the format.

---

## 7. Unknown fields (P3-T20)

**The case that matters is not a corrupt file.** It is a colleague on a newer build sending a part,
someone here opening it to check a dimension, and saving out of habit. A reader that dropped what it
could not read would delete that colleague's work with no error and nothing to notice until much
later.

**Kept fields live on the `Document`, not at the file boundary** — because the boundary is exactly
where they would be lost. Anything held beside the document is dropped by the first edit, and
editing is what the person who opened the file does.

**Preservation reaches every level the schema has**: the document, each feature, each parameter, each
parameter inside a feature, each body, each piece of reference geometry, and the properties.

**A field is read before its owner necessarily has a name.** A map has no defined order, so a
parameter's unknown field can arrive before the parameter's name and a feature's before its id.
Fields are therefore collected with owners *relative to whatever is being read* and prefixed on the
way out. **Getting that wrong does not lose data — it doubles it**: a nested field filed under its
container is written at both levels, and the test asserts the key appears **exactly once**, which
the obvious assertion did not catch.

**Values go back verbatim.** Re-encoding a value whose meaning is by definition unavailable is how a
preserving reader corrupts what it set out to preserve.

**Unknown fields are written after the known ones, in the order they were read**, so two saves
agree. Their original position among the known fields is not kept — that would mean recording an
index that stops meaning anything the moment the schema gains a field.

---

## 8. Invariants worth not breaking

1. **The bytes are a function of the document.** Anything that reads a clock, a locale, an
   environment newline, or an unordered collection breaks it.
2. **Sort every collection that has no order of its own**, on write.
3. **The reader collects before it assembles.** Map order is not defined.
4. **A migration never re-encodes what it does not understand.**
5. **Never skip a gap in the migration chain.**
6. **Unknown fields belong on the document**, not beside it.
7. **A nested unknown field is written once, at its own level.**
8. **Caches are regenerable; the graph is not.** Never make the reader depend on a cache.
9. **Raising `SchemaVersion` means: a migration, and a fixture, in the same commit.**
10. **`FormatVersion` and `SchemaVersion` are different questions.** Do not bump one for the other.

---

## 9. What §5.8's rules will cost, and when

Everything in section 5 and 6 is machinery with, at present, **nothing to migrate**. That is the
intended state and not a gap — but it means the chain has never run in anger, and the corpus has one
entry.

The first real version bump is when this subsystem is actually tested. At that point:

- A migration step is written and registered in the same commit as the bump.
- A fixture saved by the *previous* released version joins the corpus, permanently.
- Every existing fixture must still open, which is what the gate is for.

Until a release exists there is no "previous released version", so schema 1 can still be edited in
place — as P3-T21 did when it added `settings` to the feature schema without a bump, deliberately
and recorded as such. **After the first release the same change needs a bump and a migration**, and
that is the moment this document stops describing a mechanism and starts describing an obligation.

P5-T03 made two more such edits, both additive and both recorded here for the same reason:

- an optional `prop` field on an entity reference, naming which of its feature's declared inputs it
  satisfies. Written **only when present**, so a file re-saved by this build does not grow an empty
  string in every reference — §3 requires a re-save to be bit-identical, and an
  unconditional field would break it for every document written before the field existed.
- setting tag `5`, a pointer at reference geometry, holding the owner's id and the name.

P5-T04 added two more, on the same terms:

- `kind` on the document, always written. A file without it reads as a part, which is what every
  file written before it was.
- `assembly`, written **only for an assembly**, holding the component definitions and the
  placements. A part carries no such section, because §3 wants the bytes to be a function of the
  document and an empty section in every part file would change all of them to say nothing.

A reader refuses an assembly that places a component it does not list, turning `Assembly`'s own
invariant into a statement about the file. That is the one place where a malformed document is
rejected rather than preserved, and deliberately: every reader of the structure is written against
that invariant, so letting it through would hand each of them a placement with no definition to
resolve.

A reader of this build that meets an older file finds neither and is right to: an absent `prop`
means the reference does not say which input it is, which is exactly what those files meant.

Going the other way is not symmetrical, and the asymmetry is worth knowing. An older build reading a
file from this one skips the `prop` field harmlessly — it only loses which input a reference
answers, which that build had no use for. But an unrecognised **setting tag is dropped, not
preserved**: §7's unknown-field preservation covers fields, and a setting of an unknown tag is
skipped so it is not guessed at. So an older build that opened and re-saved a document containing a
datum would silently lose the datum's pointer at what it was built on. That is the ordinary cost of
editing schema 1 in place, and it stops being acceptable at the first release, which is what the
bump-and-migrate rule above exists to force.

---

## 10. Not yet done

| Gap | Why |
|---|---|
| Real migrations | Nothing to migrate; schema 1 is the only version. The chain is proved by test-declared migrations. |
| More than one format fixture | Same reason: no released version has produced one. |
| Autosave and crash-recovery journaling | §5.8 places it in Phase 6. |
| LOD levels in the tessellation cache | The container has the slot; P2-T04's LOD is not built, and `rendering.md` §11 records why. |
| Content-addressed staleness | `FileDocumentStore` stamps a document with its modification time and length, which is cheap enough to ask about every dependency when an out-of-date indicator is drawn. Two edits inside the filesystem's timestamp granularity that leave the length unchanged are indistinguishable. A vault-backed `IDocumentStore` should stamp with the revision the vault already knows; a bare filesystem has nothing better that is not a full read. |
| Per-configuration previews | Configurations are Phase 14. |

# v5 Scope: Record Editing & Saving (Schema-Driven Round-Trip Write)

**Status:** Hypothetical / not started. Captured 2026-06-25 from a design discussion held while
building the v4 schema-driven multi-game reader. **Read-only remains the current scope.** This
document records *why the v4 architecture deliberately keeps the door open for editing* and what a
future v5 would still owe. (Mirrors the format of the former `docs/v3-scope.md`, which was pruned in a
docs cleanup — see CHANGELOG.)
**Owner:** slfx77
**Last updated:** 2026-08-13 (FO4 + Skyrim read-side form-version slices and round-trip claim audit)

## Vision

Let the user edit record fields in the Records / Actors tabs and save the result back to a loadable
plugin (ESM/ESP), across every supported game (Morrowind → Fallout 76), **without a per-game
hand-written encoder**.

Editing is the natural mirror of the v4 reader: a schema describes a *byte layout*. The same per-game
schema should eventually drive decoding and encoding — one source of truth, both directions. The
FO4 and reviewed Skyrim generated read paths now retain and apply `wbFromVersion` inclusive minimum
gates. Skyrim also applies its `wbBelowVersion(35)` exclusive `SNDR.FNAM` ceiling and the literal
two-arm `ECZN.DATA` form-version decider (`<34` / `>=34`). That does not make the model bidirectional:
other games need reviewed regeneration and there is no generic writer or captured byte value for
every opaque/unused member.

## Why this is a v5 feature

- It builds *on top of* the v4 schema-driven multi-game reader — there is nothing to edit until records
  are read richly and correctly across games.
- It introduces the first *write* path to game files from the GUI (today the app is read-only plus the
  offline DMP→ESM converter). Writing crosses integrity concerns — headers, masters, FormID allocation —
  that deserve a clean version boundary, not an opportunistic patch onto v4.
- It is genuinely large: the cross-record integrity layer is what xEdit spent years on. Scoping it as v5
  keeps it from derailing v4's read-only multi-game goal.

## Why the v4 architecture enables it (the load-bearing decisions)

The v4 reader is schema-driven specifically so a writer is a *mirror*, not a parallel rewrite. Three
properties of `RecordSchema`
([Core/Formats/Esm/RecordModel/Schema/RecordSchema.cs](../src/BethesdaMultitool/Core/Formats/Esm/RecordModel/Schema/RecordSchema.cs))
were chosen with round-trip in mind:

- **Read-side gap visibility, not lossless editing.** Unmatched signed subrecords stay visible as raw
  nodes. An unmodeled inline `RawMemberDef` exposes the remaining tail as one raw node; `UnusedDef`
  advances without retaining an individual byte value. No generic re-emitter currently exists.
- **Dynamic counts modeled.** `ArrayDef.Count` / `CountRef` capture length-prefixed and count-referenced
  arrays, so a writer can recompute counts after an edit.
- **One source of truth, both directions.** Reader and a future writer should walk the same
  `RecordDef`. The FO4 and reviewed Skyrim readers carry inclusive lower gates; Skyrim's sole
  `wbBelowVersion` wrapper is an exclusive upper gate, while the direct `ECZN` decider contributes
  complementary lower/upper arm gates. Both use the nullable semantic header form version; write-side
  selection and the remaining conditional families still need an explicit contract.

**Read-side foundations and remaining prerequisites:**
- decoded nodes remain ordered, but byte-faithful re-serialization has not been demonstrated;
- original bytes still need explicit capture per opaque/unused member before verbatim passthrough is possible;
- `DetectedMainRecord.FormVersion` now distinguishes unknown/absent from known zero and reaches both
  generic decode paths; FO4 and Skyrim generated outputs have received reviewed refreshes. Skyrim's
  ten emitted minimum gates, two emitted exclusive upper gates, `MOVT` v27/v28 boundary,
  `SNDR.FNAM` v34/v35 boundary, and exact `ECZN.DATA` v33/v34 arm switch are pinned. The remaining
  `IsSSE` wrapper is edition-keyed, not a v43 form-version gate: its nested `wbFromVersion(43)` arm
  cannot be lowered safely until the reader carries a distinct LE/SE identity, so it intentionally
  remains raw. Other-game regeneration and write-side member selection remain open.

## Current state — what already exists and is reusable

| Subsystem | Location | Reusable for v5 |
|---|---|---|
| Schema model | [RecordSchema.cs](../src/BethesdaMultitool/Core/Formats/Esm/RecordModel/Schema/RecordSchema.cs) | Partial — `RecordDef` drives ordered decode; raw tails remain visible, but unused bytes are not individually retained and no generic schema-driven encoder exists |
| Schema generator | [tools/EsmSchemaGen](../tools/EsmSchemaGen) | Partial — retains inclusive `wbFromVersion` minimums, exclusive `wbBelowVersion` ceilings, and exact literal two-arm `wbFormVersionDecider` gates; FO4 and Skyrim are reviewed and runtime-gated, while Skyrim's platform wrapper and reviewed regeneration of other games remain |
| Write-side precedent | [Conversion/Processing/EsmRecordWriter.cs](../src/BethesdaMultitool/Core/Formats/Esm/Conversion/Processing/EsmRecordWriter.cs), `EsmGrupWriter.cs`, `RecordHeaderProcessor.cs` | Yes — the DMP→ESM converter already writes records, GRUPs, and headers; the schema-driven writer reuses this framing |
| Compression | `EsmRecordCompression` (Conversion) | Yes — re-compress modified Skyrim/FO4 records on save |
| Localized strings (read) | `LocalizedStringTables` + `.STRINGS` loader | Partial — read side exists; v5 needs the matching string-table *writer* |
| FormID resolver | `FormIdResolver` (App) | Yes — powers a FormID picker / reference editing in the property panel |
| Round-trip validation pattern | completeness gates (`SubrecordCompletenessTests`) + parity-harness conventions | Yes — mirror them as save-side round-trip gates |

## Scope

### In scope for v5

- Schema-driven **writer** mirroring the v4 reader: decoded tree (+ edits) + schema → record bytes.
- Within-record editing of modeled fields (integers, floats, strings, FormIDs, enums/flags, struct and
  array members).
- Verbatim passthrough of unmodeled (`RawMemberDef`) and `Unused` bytes.
- Size/count recomputation: subrecord lengths, count fields, record `DataSize`, GRUP sizes.
- Save to a **new** plugin file (non-destructive; never overwrite the source in place by default).
- Tiered round-trip validation (mirror the read-only gates): unmodified records re-emit byte-identical
  (uncompressed) or decompressed-payload-identical (compressed); modified records re-serialize
  structurally and re-open cleanly.

### Out of scope (deferred / later)

- Adding / removing / renumbering records and FormID allocation (start with edit-in-place of existing
  records only).
- Master (.esm) dependency management and masters/ONAM bookkeeping.
- Conflict resolution / record overriding across a load order (xEdit's core competency).
- Full undo/redo + transactional edit model (start with edit-one-field → save).

## Architecture — phased plan

### Phase 0 — Writer mirrors reader (within-record)

Walk the `RecordDef`; for each member, write the (possibly edited) decoded value; emit
`RawMemberDef`/`Unused` from their captured original bytes; recompute subrecord frame sizes as you go.
Reuse the converter's write-side framing and the byte-parity round-trip groundwork.

### Phase 1 — Header & container bookkeeping

Recompute record `DataSize`, GRUP `GroupSize`, and the TES4 header record/group counts + `NextObjectId`
on save. Validate the file re-opens in our own reader and in the engine / GECK / Creation Kit.

### Phase 2 — Compression & localized strings

- Compressed records (Skyrim/FO4): re-compress on write (output is *semantically* round-trip, **not**
  byte-identical to the original compressed blob). Reuse `EsmRecordCompression`.
- Localized strings (Skyrim/FO4): editing text edits the `.STRINGS`/`.ILSTRINGS`/`.DLSTRINGS` table
  (string index → entry), not just the record — needs a string-table *writer* alongside the read-side
  `LocalizedStringTables`.

### Phase 3 — GUI edit affordances

Make property-panel fields editable (typed inputs per `PrimType`, enum/flag pickers, FormID picker via
the existing resolver), with dirty-tracking and a Save action. Read-only stays the default; editing is
opt-in.

### Phase 4 — Cross-record integrity (the big one)

New / cloned records, FormID allocation + renumbering, masters management. This is the genuinely large
investment (xEdit-scale); gate it behind explicit demand.

## Open risks

- **Byte-identical save is impossible** for compressed / localized-string games — set expectations on
  "semantic round-trip," not byte-for-byte reproduction of the original file.
- **Dependent fields:** editing a value that feeds a union decider or an array count must keep dependents
  consistent. The schema carries the decider name and count refs; the writer must honor them.
- **Per-game write quirks:** the FNV Xbox→PC conversion already surfaced mixed-endian / little-endian-
  stored-FormID cases. PC-side editing largely sidesteps this (PC files are little-endian) — but it is a
  reminder that "the schema knows the layout" is necessary, not always sufficient.

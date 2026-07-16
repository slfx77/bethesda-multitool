# v5 Scope: Record Editing & Saving (Schema-Driven Round-Trip Write)

**Status:** Hypothetical / not started. Captured 2026-06-25 from a design discussion held while
building the v4 schema-driven multi-game reader. **Read-only remains the current scope.** This
document records *why the v4 architecture deliberately keeps the door open for editing* and what a
future v5 would still owe. (Mirrors the format of the former `docs/v3-scope.md`, which was pruned in a
docs cleanup — see CHANGELOG.)
**Owner:** slfx77
**Last updated:** 2026-06-25

## Vision

Let the user edit record fields in the Records / Actors tabs and save the result back to a loadable
plugin (ESM/ESP), across every supported game (Morrowind → Fallout 76), **without a per-game
hand-written encoder**.

Editing is the natural mirror of the v4 reader: a schema describes a *byte layout*, which is inherently
bidirectional. The same per-game, version-gated schema that drives decoding drives encoding — one
source of truth, both directions.

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

- **No-data-loss decode.** `RawMemberDef` and `UnusedDef` are preserved verbatim — "coverage gaps never
  lose data, only structure." A record can be edited and re-saved even where the schema only *partially*
  models it: known fields change; unmodeled bytes pass through byte-for-byte.
- **Dynamic counts modeled.** `ArrayDef.Count` / `CountRef` capture length-prefixed and count-referenced
  arrays, so a writer can recompute counts after an edit.
- **One source of truth, both directions.** Reader and writer walk the same `RecordDef`; the
  version-gating added in v4 tells the writer exactly which fields to emit for a given record version.

**Cheap choices made during v4 (read-only) that keep v5 a small addition rather than a rewrite — make
these now:**
- decode into an *ordered, structurally faithful* tree (preserves member order on re-serialize);
- keep *original bytes per opaque/unused member* (verbatim passthrough);
- capture the record's *form version* at decode time.

## Current state — what already exists and is reusable

| Subsystem | Location | Reusable for v5 |
|---|---|---|
| Bidirectional schema | [RecordSchema.cs](../src/BethesdaMultitool/Core/Formats/Esm/RecordModel/Schema/RecordSchema.cs) | Yes — same `RecordDef` drives decode and encode; `RawMemberDef`/`UnusedDef` verbatim; `ArrayDef.CountRef` for dynamic counts |
| Schema generator | [tools/EsmSchemaGen](../tools/EsmSchemaGen) | Yes — produces the per-game, version-gated schemas; no hand-written per-game encoders |
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

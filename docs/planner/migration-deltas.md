# ESP Planner — migration deltas

Per the plan, the planner pipeline is deliberately allowed to produce different output
from the legacy pipeline when (and only when) the difference fixes a real bug or
documents an unavoidable cross-pipeline timing artifact. Every such delta lives here,
with the user's signoff date. `LegacyVsPlannerParityTests` consults this list when a
byte diff appears for a record type.

Until a tier introduces a delta, this file just describes the *categories* of expected
deltas so reviewers know what shape future entries will take.

## Open deltas

_None recorded yet — Tiers 1–5a all ship strict byte-exact for every ported type._

> The C# `MigrationDeltaRegistry.Default` mirrors this table; `MigrationDeltaMarkdownSyncTests` keeps the two lists in step by DELTA-NNN id.

## Tier 5b — cell-children pipeline (architectural gap)

The per-encoder migration pattern doesn't carry the rest of the way. Tier 5b is
**partial**: it ships `PlannedCellEncoder` plus a shared `PlannedPlacedReferenceEncoder`
covering REFR/ACHR/ACRE, but those encoders are registered without any dispatch path
routing through them.

### What's missing

- **CellGrupBuilder integration.** The legacy pipeline emits cell-children records
  (REFR/ACHR/ACRE in persistent/temporary/VWD children GRUPs) through
  `CellGrupBuilder`, not through `BuildGrupForType`. The dispatch shim in
  `PluginBuilder.BuildGrupForType` therefore never invokes the planned cell-children
  encoders. Routing cell-children emission through `PlanWriter` requires modifying
  `CellGrupBuilder` to consult `_planWriter` when the user enables CELL / REFR / ACHR
  / ACRE through `PlannerEnabledRecordTypes`. The plumbing parallels what
  `BuildGrupForType` already does for top-level GRUPs.

- **LAND / NAVM / NAVI / PGRE encoders.** None of these have standard `IRecordEncoder`
  paths in the legacy code:
    - LAND emits via `LandOverrideBuilder` → `LandEncoder.Encode(LandHeightmap,
      LandVisualData?)`. The encoder signature is fundamentally different from the
      single-model `IPlannedRecordEncoder<TModel>` contract — it takes two structural
      inputs that come from cell context, not a single record model.
    - NAVM / NAVI are emitted by `NavInfoMapBuilder` / `NavMeshOverrideBuilder` with
      cross-record dependencies (NAVI indexes NAVMs, NAVMs reference cell context).
      These need a tree-shaped abstraction the current encoder interface does not
      provide.
    - PGRE (placed grenade) — encoder primitive + planner wrapper landed in Tier 7a
      (`PgreEncoder` / `PlannedPgreEncoder`); cell-children dispatch routing still
      pending because `PlacedGrenadeRecord` doesn't yet carry a parent-cell FormID
      and `PlanCellSectionBuilder` doesn't iterate the PGRE bucket.

- **CELL.New disposition.** `CellEncoder.Encode` only emits the override-mutable
  subrecords (DATA/XCLC); new CELLs require the full subrecord stream that
  `CellGrupBuilder` constructs as part of building the parent GRUP. The planner
  representation needs a `RecordPlan` shape that carries cell context (parent WRLD,
  block / sub-block labels, persistent / temporary / VWD children) into the writer.

### Why ship the partial encoders now

So the encoder slots exist when the cell-pipeline refactor lands. The
`PlannedPlacedReferenceEncoder` already produces byte-identical output to legacy for
synthetic test records — three parity tests pin that. Wiring is a one-line swap in
`CellGrupBuilder` once that refactor begins.

## Categories

### Cross-pipeline FormID-allocation shift (anticipated, not yet active)

When at least one record type is opted into the planner, the planner runs its allocation
phase **before** the legacy pipeline's per-type allocation loop. New records of
planner-owned types get their plugin-range FormIDs from the front of the allocator's
range; everything legacy emits after that point gets shifted upward by the planner's
allocation count.

Practical effects:

- A run with `PlannerEnabledRecordTypes = {STAT}` and N new STATs in the DMP allocates
  those STATs at `0x01000800..0x010008(N-1)`. A subsequent legacy allocation for, say,
  a new GMST starts at `0x010008N` instead of `0x01000800`.
- The bytes for *planner-owned* records are byte-identical to what legacy would produce
  for the same model with the same FormID — Tier 1 parity tests pin that. The shift
  only affects the *legacy-owned* GRUPs' FormID fields.
- Once the legacy pipeline is deleted (after Tier 5), the shift disappears entirely:
  there's only one allocator, run once, in deterministic order.

For Tier 1, no in-tree DMP fixture exercises both pipelines on the same allocator at
once, so this is theoretical for now. The first tier that triggers it (Tier 2 once
multiple record types share the allocator state and new records appear in real DMPs)
will record a concrete delta entry here.

### Reference-resolution improvements (Tier 3+)

The first concrete category of deliberate behavior change. The planner's reference
resolver drops or downgrades references whose targets aren't actually in the emit set,
preventing the v54 "Script will not be executed" cascade where legacy emitted dangling
SCROs into master records. Per-record deltas will describe each affected record type.

## Format for new entries

```
## DELTA-NNN: <one-line title>
**Tier**: <which tier introduced this>
**Record types affected**: <list>
**Behavior under legacy**: <what legacy does>
**Behavior under planner**: <what planner does>
**In-game effect**: <user-visible behavior change>
**Approval**: <user signoff date>
```

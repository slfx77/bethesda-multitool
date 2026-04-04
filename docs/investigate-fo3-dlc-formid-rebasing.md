# Investigation: Fallout 3 DLC FormID Rebasing for FNV Comparison

## Problem

When Fallout 3 DLC content was carried into Fallout New Vegas, the FormIDs were rebased from DLC load-order prefixes (`01`–`05`) to the base ESM prefix (`00`). The cross-build comparison tool doesn't account for this, so DLC records never match their FNV counterparts.

### Example

- **FO3 DLC01 (Anchorage)**: `WeapSteelSaw` has FormID `0x01092BDC` (prefix `01` = first DLC)
- **FNV**: The same asset reused in FNV has FormID `0x00092BDC` (prefix `00` = base ESM)

The current merge keeps the `01` prefix, so `0x01092BDC` never matches `0x00092BDC` in the comparison. The record appears as base-only in FO3 and as NEW in FNV, when it should show as a continuous evolution.

## Context

### ESM Load Order and FormID Prefixes

In Bethesda's engine, FormIDs are prefixed with the load order index:
- `00` = base game ESM (Fallout3.esm or FalloutNV.esm)
- `01` = first DLC/plugin
- `02` = second DLC/plugin
- etc.

When a DLC ESM defines a new record, its FormID uses the DLC's load order prefix. But when that content is later incorporated into a new base game (FO3 → FNV), the FormIDs are rebased to `00`.

### Which DLC Records Were Carried to FNV?

Not all FO3 DLC content was reused. Only specific assets (weapons, armor, NPCs) were carried over. The matching is by the lower 24 bits of the FormID (the actual record ID, ignoring the load order prefix).

### MAST Subrecords and Load Order

Each DLC ESM lists its masters in MAST subrecords:
- `Anchorage.esm`: MAST = `Fallout3.esm` (load order index 1)
- `BrokenSteel.esm`: MAST = `Fallout3.esm` (load order index 1)
- etc.

The load order prefix in FormIDs corresponds to the position in the MAST list + 1 (0 = self for new records in that DLC, but records referencing the master use the master's index).

Actually, for DLC-defined records: the **high byte** of the FormID matches the **position of the defining file** in the combined load order. For a DLC with one master (Fallout3.esm at index 0), the DLC itself is at index 1, so its new records have prefix `01`.

## Proposed Solution

When merging FO3 DLCs into the base build, strip the load order prefix from DLC FormIDs:

```
Merged FormID = (originalFormId & 0x00FFFFFF)
```

This rebases all DLC records to the `00` prefix, matching how FNV references them.

### Implementation Location

**`src/FalloutXbox360Utils/CLI/Commands/Dmp/DmpCompareCommand.cs`** — `ProcessBaseDirectoryAsync` method

After parsing each DLC ESM, before merging into the base RecordCollection:
1. Detect which records have non-zero high bytes (DLC-defined records)
2. Remap their FormIDs: `newFormId = oldFormId & 0x00FFFFFF`
3. Also remap any internal FormID references (base object refs, faction refs, etc.)

### Complexity Warning

FormID rebasing is not trivial:
- **Record FormIDs** need remapping (the record's own FormID)
- **Internal references** need remapping (e.g., a weapon's ProjectileFormId, an NPC's FactionFormId)
- **Collision detection** needed — a rebased DLC FormID might collide with an existing base game FormID
- **Selective rebasing** — only records that were actually carried to FNV should be rebased; others can be left as-is since they'll be filtered out anyway

### Simpler Alternative

Instead of rebasing FormIDs in the parser, do it in the aggregator:
1. After aggregation, scan for records where FormID `0x00XXXXXX` exists in FNV builds and `0x01XXXXXX`–`0x05XXXXXX` exists in the FO3 base
2. Merge them: treat the DLC version as the base build's entry for that FormID
3. This avoids modifying the parser and keeps the rebasing logic isolated

## Where to Investigate

1. **`RecordCollection.MergeWith()`** — `src/FalloutXbox360Utils/Core/Formats/Esm/Models/RecordCollection.cs` lines 238-324
   - Understand how FormID collisions are handled during merge
   - Check if there's already a FormID remapping mechanism

2. **`CrossDumpAggregator.Aggregate()`** — `src/FalloutXbox360Utils/Core/Formats/Esm/Export/CrossDumpAggregator.cs`
   - The post-aggregation approach would add rebasing logic here
   - After building the index, scan for `0x00XXXXXX` ↔ `0x01XXXXXX` matches

3. **FO3 DLC FormID ranges** — Determine which prefix corresponds to which DLC:
   - `01` = Anchorage (first MAST entry after Fallout3.esm)
   - `02` = BrokenSteel, `03` = PointLookout, `04` = ThePitt, `05` = Zeta
   - But the actual prefix depends on load order, which may vary

## Verification

1. Find a known shared asset: `WeapSteelSaw` = `0x01092BDC` in FO3 Anchorage, should be `0x00092BDC` in FNV
2. Check if `0x00092BDC` exists in any FNV DMP or ESM
3. After implementing rebasing, verify the record shows as BASE→CHANGED (FO3→FNV) instead of two separate unlinked records
